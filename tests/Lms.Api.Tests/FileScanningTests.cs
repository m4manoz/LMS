using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Lms.Api.Infrastructure.Storage;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>A stand-in for the ClamAV daemon that speaks its INSTREAM protocol and calls any file containing the marker harmful.</summary>
internal sealed class FakeClamd : IDisposable
{
    public const string Marker = "FAKE-VIRUS-MARKER";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int Scans;
    public List<int> ScannedSizes { get; } = [];

    public FakeClamd()
    {
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => HandleAsync(client)); }
                catch { return; }
            }
        });
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            if (Encoding.ASCII.GetString(command) != "zINSTREAM\0") { await stream.WriteAsync(Encoding.ASCII.GetBytes("UNKNOWN COMMAND\0")); return; }
            var data = new MemoryStream();
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0) break;
                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk);
                data.Write(chunk);
            }
            Interlocked.Increment(ref Scans);
            lock (ScannedSizes) ScannedSizes.Add((int)data.Length);
            var harmful = Encoding.UTF8.GetString(data.ToArray()).Contains(Marker);
            await stream.WriteAsync(Encoding.ASCII.GetBytes((harmful ? "stream: Fake.Test.Signature FOUND" : "stream: OK") + "\0"));
        }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

public sealed class ScannerUnitTests
{
    [Theory]
    [InlineData("stream: OK", true, null)]
    [InlineData("stream: Eicar-Test-Signature FOUND", false, "Eicar-Test-Signature")]
    [InlineData("stream: Win.Trojan.Agent-1234 FOUND", false, "Win.Trojan.Agent-1234")]
    public void The_daemons_answer_is_understood(string answer, bool clean, string? threat)
    {
        var result = ClamAvScanner.Parse(answer);
        Assert.Equal((clean, threat), (result.Clean, result.Threat));
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("")]
    [InlineData("something unexpected")]
    public void An_error_or_a_silent_daemon_is_not_a_verdict(string answer) => Assert.Throws<ScanUnavailableException>(() => ClamAvScanner.Parse(answer));

    [Fact]
    public async Task The_real_scanner_streams_a_file_in_chunks_and_reads_the_verdict()
    {
        using var daemon = new FakeClamd();
        var scanner = new ClamAvScanner(new FileScanOptions { Provider = "ClamAv", Host = "127.0.0.1", Port = daemon.Port });
        var big = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 300_000)));   // several chunks
        Assert.True((await scanner.ScanAsync(big, CancellationToken.None)).Clean);
        Assert.Equal(300_000, daemon.ScannedSizes.Single());
        var bad = await scanner.ScanAsync(new MemoryStream(Encoding.UTF8.GetBytes("before " + FakeClamd.Marker + " after")), CancellationToken.None);
        Assert.Equal((false, "Fake.Test.Signature"), (bad.Clean, bad.Threat));
    }

    [Fact]
    public async Task An_unreachable_daemon_is_reported_as_unavailable()
    {
        var port = FreePort();
        var scanner = new ClamAvScanner(new FileScanOptions { Provider = "ClamAv", Host = "127.0.0.1", Port = port, TimeoutSeconds = 2 });
        await Assert.ThrowsAsync<ScanUnavailableException>(() => scanner.ScanAsync(new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    [Fact]
    public void The_scan_settings_are_checked()
    {
        Assert.Empty(new FileScanOptions().Validate());
        Assert.Empty(new FileScanOptions { Provider = "clamav", Host = "scanner", Port = 3310, OnError = "allow" }.Validate());
        Assert.Contains(new FileScanOptions { Provider = "Magic" }.Validate(), item => item.Contains("None or ClamAv"));
        Assert.Contains(new FileScanOptions { Provider = "ClamAv", Host = "" }.Validate(), item => item.Contains("Host"));
        Assert.Contains(new FileScanOptions { Provider = "ClamAv", Port = 0 }.Validate(), item => item.Contains("Port"));
        Assert.Contains(new FileScanOptions { OnError = "Maybe" }.Validate(), item => item.Contains("Reject or Allow"));
    }
}

/// <summary>Uploads go through the scanner wherever they happen in the app.</summary>
public sealed class FileScanningTests : IDisposable
{
    private readonly FakeClamd _daemon = new();
    private readonly List<LmsApiFactory> _factories = [];

    private TestWorld WorldWith(string onError = "Reject", int? port = null)
    {
        var factory = new LmsApiFactory
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["Storage:VirusScan:Provider"] = "ClamAv", ["Storage:VirusScan:Host"] = "127.0.0.1", ["Storage:VirusScan:Port"] = (port ?? _daemon.Port).ToString(),
                ["Storage:VirusScan:TimeoutSeconds"] = "3", ["Storage:VirusScan:OnError"] = onError,
            }
        };
        _factories.Add(factory);
        return new TestWorld(factory);
    }

    public void Dispose() { _daemon.Dispose(); foreach (var factory in _factories) factory.Dispose(); }

    private static MultipartFormDataContent File(string name, string text, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", name);
        foreach (var (key, value) in fields) content.Add(new StringContent(value), key);
        return content;
    }

    private static string Url(Guid threadId) => $"/api/v1/tenant/community/threads/{threadId}/attachments";

    private static async Task<Guid> ThreadAsync(Person by)
    {
        var response = await by.Client.PostAsJsonAsync("/api/v1/tenant/community/threads", new { title = "A question", body = "Please help." });
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_clean_file_is_checked_and_accepted_and_a_harmful_one_is_refused_and_not_kept()
    {
        var world = WorldWith();
        var t = await world.NewTenantAsync();
        var lena = await world.AddLearnerAsync(t, "Lena");
        var thread = await ThreadAsync(lena);

        Assert.Equal(HttpStatusCode.Created, (await lena.Client.PostAsync(Url(thread), File("notes.txt", "perfectly fine"))).StatusCode);
        Assert.Equal(1, _daemon.Scans);

        var refused = await lena.Client.PostAsync(Url(thread), File("bad.txt", $"hello {FakeClamd.Marker} world"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await ReadAsync(refused);
        Assert.Contains("harmful", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("fileRejected").GetBoolean());
        Assert.Single((await ReadAsync(await lena.Client.GetAsync($"/api/v1/tenant/community/threads/{thread}"))).GetProperty("attachments").EnumerateArray());   // only the clean one
    }

    [Fact]
    public async Task Every_kind_of_upload_is_scanned()
    {
        var world = WorldWith();
        var t = await world.NewTenantAsync();
        var course = await world.NewCourseAsync(t, "SCAN");
        var lena = await world.AddLearnerAsync(t, "Lena");
        var tara = await world.AddPersonAsync(t, "Tara", "TEACHER");
        Assert.True((await EnrollAsync(lena, course)).IsSuccessStatusCode);
        var harmful = $"x {FakeClamd.Marker} x";

        // An assignment submission.
        var assignment = (await ReadAsync(await t.Admin.PostAsJsonAsync("/api/v1/tenant/assignments", new { courseId = course.Id, title = "Scanned work", maxPoints = 10 }))).GetProperty("id").GetGuid();
        await t.Admin.PostAsync($"/api/v1/tenant/assignments/{assignment}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await lena.Client.PostAsync($"/api/v1/tenant/assignments/{assignment}/submission", File("w.txt", harmful, ("text", "my work")))).StatusCode);
        Assert.Empty((await ReadAsync(await t.Admin.GetAsync($"/api/v1/tenant/assignments/{assignment}/submissions"))).EnumerateArray());   // nothing was recorded

        // A message.
        var conversation = (await ReadAsync(await lena.Client.PostAsJsonAsync("/api/v1/tenant/messages/conversations/direct", new { userId = tara.Id }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await lena.Client.PostAsync($"/api/v1/tenant/messages/conversations/{conversation}/messages/upload", File("m.txt", harmful))).StatusCode);
        Assert.Empty((await ReadAsync(await lena.Client.GetAsync($"/api/v1/tenant/messages/conversations/{conversation}/messages"))).EnumerateArray());

        // A lesson file.
        var lesson = course.LessonIds[0][0];
        var draft = await world.NewCourseAsync(t, "SCAN2", publish: false);
        var block = new MultipartFormDataContent { { new StringContent("Download"), "type" } };
        block.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(harmful)), "file", "template.docx");
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsync($"/api/v1/tenant/courses/{draft.Id}/lessons/{draft.LessonIds[0][0]}/blocks", block)).StatusCode);
        _ = lesson;

        Assert.True(_daemon.Scans >= 3);
    }

    [Fact]
    public async Task When_the_scanner_is_down_uploads_are_refused_by_default()
    {
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var world = WorldWith(port: port);
        var t = await world.NewTenantAsync();
        var lena = await world.AddLearnerAsync(t, "Lena");
        var thread = await ThreadAsync(lena);
        var refused = await lena.Client.PostAsync(Url(thread), File("a.txt", "fine"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("could not be checked", (await ReadAsync(refused)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_organization_can_choose_to_accept_files_when_the_scanner_is_down()
    {
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var world = WorldWith(onError: "Allow", port: port);
        var t = await world.NewTenantAsync();
        var lena = await world.AddLearnerAsync(t, "Lena");
        var thread = await ThreadAsync(lena);
        Assert.Equal(HttpStatusCode.Created, (await lena.Client.PostAsync(Url(thread), File("a.txt", "fine"))).StatusCode);
    }

    [Fact]
    public async Task Without_a_scanner_nothing_changes()
    {
        using var factory = new LmsApiFactory();
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var lena = await world.AddLearnerAsync(t, "Lena");
        var thread = await ThreadAsync(lena);
        Assert.Equal(HttpStatusCode.Created, (await lena.Client.PostAsync(Url(thread), File("a.txt", $"{FakeClamd.Marker} is just text here"))).StatusCode);
        Assert.Equal(0, _daemon.Scans);
    }

    [Fact]
    public void A_misspelt_scanner_setting_stops_the_host_from_starting()
    {
        using var factory = new LmsApiFactory { ExtraSettings = new Dictionary<string, string?> { ["Storage:VirusScan:Provider"] = "Magic" } };
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("virus scan configuration is invalid", exception.ToString());
    }
}

public sealed class BucketSelectionTests
{
    private static readonly Guid Acme = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static S3StorageOptions Options(params (string Tenant, string Bucket)[] own)
    {
        var settings = new Dictionary<string, string?> { ["Storage:S3:Bucket"] = "shared" };
        foreach (var (tenant, bucket) in own) settings[$"Storage:S3:TenantBuckets:{tenant}"] = bucket;
        return S3StorageOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void An_organization_with_its_own_bucket_uses_it_and_everyone_else_shares()
    {
        var options = Options((Acme.ToString("D"), "acme-files"));
        Assert.Equal("acme-files", options.BucketFor($"{Acme:D}/{Guid.NewGuid():D}/file.pdf"));
        Assert.Equal("shared", options.BucketFor($"{Other:D}/{Guid.NewGuid():D}/file.pdf"));
        Assert.Equal("acme-files", options.BucketForTenant(Acme));
        Assert.Equal("shared", options.BucketForTenant(Other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid/file.pdf")]
    [InlineData("/11111111-1111-1111-1111-111111111111/file.pdf")]
    [InlineData("file.pdf")]
    public void Keys_that_do_not_start_with_an_organization_id_go_to_the_shared_bucket(string key)
        => Assert.Equal("shared", Options((Acme.ToString("D"), "acme-files")).BucketFor(key));

    [Fact]
    public void The_organization_id_matches_whatever_its_letter_case_or_form()
    {
        var options = Options((Acme.ToString("D").ToUpperInvariant(), "acme-files"));
        Assert.Equal("acme-files", options.BucketFor($"{Acme:D}/x"));
        Assert.Equal("acme-files", Options((Acme.ToString("N"), "acme-files")).BucketFor($"{Acme:D}/x"));
    }

    [Fact]
    public void Every_bucket_is_listed_once_so_readiness_and_setup_cover_them_all()
    {
        var options = Options((Acme.ToString("D"), "acme-files"), (Other.ToString("D"), "acme-files"));
        Assert.Equal(new[] { "acme-files", "shared" }, options.AllBuckets().Order().ToArray());
        Assert.Equal(new[] { "shared" }, Options().AllBuckets().ToArray());
    }

    [Fact]
    public void Bad_bucket_settings_are_reported()
    {
        Assert.Empty(Options((Acme.ToString("D"), "acme-files")).Validate());
        Assert.Contains(Options(("acme", "acme-files")).Validate(), item => item.Contains("not an organization id"));
        Assert.Contains(Options((Acme.ToString("D"), "  ")).Validate(), item => item.Contains("needs a bucket name"));
    }
}
