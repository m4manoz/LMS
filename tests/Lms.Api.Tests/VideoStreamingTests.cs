using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lms.Api.Infrastructure.Storage;
using Lms.Api.Infrastructure.Videos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Stands in for FFmpeg: writes a small playlist, two segments, a poster and an audio file, or fails on request.</summary>
public sealed class FakeVideoTranscoder : IVideoTranscoder
{
    public bool Enabled { get; set; } = true;
    public string? FailWith { get; set; }
    public bool Poster { get; set; } = true;
    public int Calls { get; private set; }

    public Task<TranscodeResult> PackageAsync(string workDirectory, CancellationToken cancellationToken)
    {
        Calls++;
        if (FailWith is not null) throw new InvalidOperationException(FailWith);
        Assert.True(File.Exists(Path.Combine(workDirectory, VideoFiles.Input)));
        File.WriteAllText(Path.Combine(workDirectory, VideoFiles.Master), "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720\nv0-index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nv1-index.m3u8\n");
        for (var quality = 0; quality < 2; quality++)
        {
            File.WriteAllText(Path.Combine(workDirectory, VideoFiles.RenditionPlaylist(quality)), $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:6.0,\nv{quality}-seg00000.ts\n#EXTINF:2.0,\nv{quality}-seg00001.ts\n#EXT-X-ENDLIST\n");
            for (var piece = 0; piece < 2; piece++) File.WriteAllBytes(Path.Combine(workDirectory, VideoFiles.RenditionSegment(quality, piece)), [0x47, (byte)quality, (byte)piece]);
        }
        if (Poster) File.WriteAllBytes(Path.Combine(workDirectory, VideoFiles.Poster), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2]);
        return Task.FromResult(new TranscodeResult([new Rendition(720, 2), new Rendition(360, 2)], Poster, 8));
    }

    public Task ExtractAudioAsync(string workDirectory, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw new InvalidOperationException(FailWith);
        File.WriteAllBytes(Path.Combine(workDirectory, VideoFiles.Audio), [1, 2, 3, 4, 5]);
        return Task.CompletedTask;
    }
}

/// <summary>Uploads are converted to streaming pieces and a poster; playback uses signed addresses that cover every piece.</summary>
public sealed class VideoStreamingTests
{
    private const string Videos = "/api/v1/tenant/videos";

    private sealed record Setup(TestWorld World, LmsApiFactory Factory, FakeVideoTranscoder Fake, Tenant Tenant, Person Teacher, Person Ada, Person Ben, CourseInfo Course);

    private static async Task<Setup> NewSetupAsync(bool enabled = true)
    {
        var fake = new FakeVideoTranscoder { Enabled = enabled };
        var factory = new LmsApiFactory { Transcoder = fake };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var ben = await world.AddLearnerAsync(t, "Ben");
        var course = await world.NewCourseAsync(t, "STR-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(world, factory, fake, t, teacher, ada, ben, course);
    }

    private static async Task<Guid> UploadAsync(Setup s, string title = "Lecture")
    {
        var bytes = new byte[2048];
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }.CopyTo(bytes, 0);
        var form = new MultipartFormDataContent { { new StringContent(s.Course.Id.ToString()), "courseId" }, { new StringContent(title), "title" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "file", "lecture.mp4");
        var response = await s.Teacher.Client.PostAsync(Videos, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ReadyVideoAsync(Setup s) { var id = await UploadAsync(s); await ConvertAsync(s); return id; }
    private static Task<int> ConvertAsync(Setup s) => s.Factory.Services.GetRequiredService<VideoProcessingService>().RunOnceAsync(CancellationToken.None);
    private static async Task<JsonElement> GetAsync(Person who, Guid id) => await ReadAsync(await who.Client.GetAsync($"{Videos}/{id}"));
    private static HttpClient Anonymous(Setup s) => s.Factory.CreateClient();

    [Fact]
    public async Task An_upload_waits_for_conversion_and_learners_cannot_see_it_until_it_is_ready()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s);
        Assert.Equal("Processing", (await GetAsync(s.Teacher, id)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync($"{Videos}/{id}")).StatusCode);
        Assert.Empty((await ReadAsync(await s.Ada.Client.GetAsync(Videos))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync($"{Videos}/{id}/link")).StatusCode);

        Assert.Equal(1, await ConvertAsync(s));
        var video = await GetAsync(s.Ada, id);
        Assert.Equal("Ready", video.GetProperty("status").GetString());
        Assert.True(video.GetProperty("hasStreaming").GetBoolean());
        Assert.Equal(8, video.GetProperty("durationSeconds").GetInt32());   // taken from the conversion
        Assert.Contains($"/videos/{id}/poster?token=", video.GetProperty("posterUrl").GetString());
        Assert.Equal(0, await ConvertAsync(s));                              // nothing left to do
    }

    [Fact]
    public async Task Without_conversion_set_up_uploads_are_ready_straight_away_and_have_no_streaming_version()
    {
        var s = await NewSetupAsync(enabled: false);
        var id = await UploadAsync(s);
        var video = await GetAsync(s.Ada, id);
        Assert.Equal("Ready", video.GetProperty("status").GetString());
        Assert.False(video.GetProperty("hasStreaming").GetBoolean());
        Assert.Equal(JsonValueKind.Null, video.GetProperty("posterUrl").ValueKind);
        Assert.Equal("stream", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"))).GetProperty("kind").GetString());
        Assert.Equal(0, s.Fake.Calls);
    }

    [Fact]
    public async Task A_converted_video_plays_from_a_signed_playlist_whose_pieces_carry_the_same_token()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s);
        await ConvertAsync(s);
        var link = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"));
        Assert.Equal("hls", link.GetProperty("kind").GetString());
        Assert.Contains("/stream?token=", link.GetProperty("fallbackUrl").GetString());   // the original file stays available

        var anonymous = Anonymous(s);
        Assert.Contains("/hls/master.m3u8?token=", link.GetProperty("url").GetString());
        var master = await anonymous.GetAsync(link.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", master.Content.Headers.ContentType?.MediaType);
        var token = Uri.UnescapeDataString(link.GetProperty("url").GetString()!.Split("token=")[1]);
        var masterText = await master.Content.ReadAsStringAsync();
        Assert.Contains("RESOLUTION=1280x720", masterText);                 // every quality is offered
        var qualities = masterText.Split('\n').Where(line => line.StartsWith('v')).ToArray();
        Assert.Equal(2, qualities.Length);
        Assert.All(qualities, entry => Assert.EndsWith($"?token={Uri.EscapeDataString(token)}", entry));

        var playlist = await anonymous.GetAsync($"{Videos}/{id}/hls/{qualities[1]}");               // the lower quality
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        var text = await playlist.Content.ReadAsStringAsync();
        var pieces = text.Split('\n').Where(line => line.StartsWith("v1-seg")).ToArray();
        Assert.Equal(2, pieces.Length);
        Assert.All(pieces, piece => Assert.EndsWith($"?token={Uri.EscapeDataString(token)}", piece));
        Assert.Contains("#EXT-X-ENDLIST", text);

        var segment = await anonymous.GetAsync($"{Videos}/{id}/hls/{pieces[1]}");
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal(new byte[] { 0x47, 1, 1 }, await segment.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", segment.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Playlist_pieces_and_posters_refuse_missing_forged_foreign_and_odd_requests()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s);
        var other = await UploadAsync(s, "Other");
        await ConvertAsync(s);
        var link = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"));
        var token = link.GetProperty("url").GetString()!.Split("token=")[1];
        var anonymous = Anonymous(s);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/master.m3u8")).StatusCode);                 // no token
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/master.m3u8?token=forged")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{other}/hls/master.m3u8?token={token}")).StatusCode); // another video
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/v0-seg00002.ts?token={token}")).StatusCode);   // beyond the last piece
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/v2-index.m3u8?token={token}")).StatusCode);   // a quality that was not made
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{Videos}/{id}/hls/v1-seg00001.ts?token={token}")).StatusCode);
        foreach (var odd in new[] { "seg1.ts", "..%2Findex.m3u8", "input.bin", "audio.mp3", "poster.jpg", "index.m3u8", "seg00000.ts", "index.m3u8.bak", "v0-seg1.ts", "v10-index.m3u8" })
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/{odd}?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{other}/poster?token={token}")).StatusCode);

        var poster = await anonymous.GetAsync((await GetAsync(s.Ada, id)).GetProperty("posterUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, poster.StatusCode);
        Assert.Equal("image/jpeg", poster.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_fails_with_a_reason_and_can_be_retried_by_staff_only()
    {
        var s = await NewSetupAsync();
        var id = await UploadAsync(s);
        s.Fake.FailWith = "The file could not be read as a video.";
        await ConvertAsync(s);
        var failed = await GetAsync(s.Teacher, id);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Contains("could not be read", failed.GetProperty("statusMessage").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Ada.Client.GetAsync($"{Videos}/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);
        s.Fake.FailWith = null;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);   // already queued
        await ConvertAsync(s);
        Assert.Equal("Ready", (await GetAsync(s.Ada, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Converting_again_is_refused_when_not_set_up_and_for_linked_videos()
    {
        var off = await NewSetupAsync(enabled: false);
        var id = await UploadAsync(off);
        Assert.Equal(HttpStatusCode.Conflict, (await off.Teacher.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);

        var s = await NewSetupAsync();
        var external = await s.Teacher.Client.PostAsync($"{Videos}/external", JsonContent.Create(new { courseId = s.Course.Id, title = "Linked", url = "https://videos.example.org/a" }));
        var linked = (await ReadAsync(external)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsync($"{Videos}/{linked}/reprocess", null)).StatusCode);
    }

    [Fact]
    public async Task An_older_ready_upload_can_be_converted_later()
    {
        var s = await NewSetupAsync(enabled: false);
        var id = await UploadAsync(s);
        s.Fake.Enabled = true;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);
        await ConvertAsync(s);
        Assert.True((await GetAsync(s.Ada, id)).GetProperty("hasStreaming").GetBoolean());
    }

    [Fact]
    public async Task A_video_without_a_poster_has_none_and_deleting_removes_every_stored_piece()
    {
        var s = await NewSetupAsync();
        s.Fake.Poster = false;
        var id = await UploadAsync(s);
        await ConvertAsync(s);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(s.Ada, id)).GetProperty("posterUrl").ValueKind);

        var storage = s.Factory.Services.GetRequiredService<IContentAssetStorage>();
        var tenantId = Guid.Empty;
        await s.World.WithDbAsync(s.Tenant.Slug, async db => tenantId = (await db.Videos.SingleAsync()).TenantId);
        var keys = new[] { "master.m3u8", "v0-index.m3u8", "v1-index.m3u8", "v0-seg00000.ts", "v0-seg00001.ts", "v1-seg00000.ts", "v1-seg00001.ts" }.Select(name => VideoFiles.Key(tenantId, s.Course.Id, id, name)).ToArray();
        foreach (var key in keys) Assert.True(await storage.ExistsAsync(key, CancellationToken.None), key);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"{Videos}/{id}")).StatusCode);
        foreach (var key in keys) Assert.False(await storage.ExistsAsync(key, CancellationToken.None), key);
    }

    [Fact]
    public async Task The_qualities_are_remembered_best_first()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Equal("720:2,360:2", (await db.Videos.SingleAsync(item => item.Id == id)).HlsLayout));
    }

    [Fact]
    public async Task Converting_again_replaces_every_piece_of_the_old_version()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var tenantId = Guid.Empty;
        await s.World.WithDbAsync(s.Tenant.Slug, async db => tenantId = (await db.Videos.SingleAsync()).TenantId);
        var storage = s.Factory.Services.GetRequiredService<IContentAssetStorage>();
        var stale = VideoFiles.Key(tenantId, s.Course.Id, id, "v0-seg00007.ts");
        await storage.PutAsync(stale, new MemoryStream([1]), "video/mp2t", CancellationToken.None);   // a leftover that no longer belongs to the video
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var video = await db.Videos.SingleAsync(); video.HlsLayout = "720:8"; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.Accepted, (await s.Teacher.Client.PostAsync($"{Videos}/{id}/reprocess", null)).StatusCode);
        await ConvertAsync(s);
        Assert.False(await storage.ExistsAsync(stale, CancellationToken.None));
        Assert.True(await storage.ExistsAsync(VideoFiles.Key(tenantId, s.Course.Id, id, "v1-seg00001.ts"), CancellationToken.None));
    }

    [Fact]
    public async Task A_video_converted_before_there_were_several_qualities_still_plays()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var tenantId = Guid.Empty;
        await s.World.WithDbAsync(s.Tenant.Slug, async db =>
        {
            var video = await db.Videos.SingleAsync();
            tenantId = video.TenantId;
            video.HlsLayout = null; video.HlsSegmentCount = 2;                                                   // the old layout: one playlist, pieces at the top
            await db.SaveChangesAsync();
        });
        var storage = s.Factory.Services.GetRequiredService<IContentAssetStorage>();
        await storage.PutAsync(VideoFiles.Key(tenantId, s.Course.Id, id, "index.m3u8"), new MemoryStream(Encoding.ASCII.GetBytes("#EXTM3U\n#EXTINF:6.0,\nseg00000.ts\n#EXTINF:2.0,\nseg00001.ts\n#EXT-X-ENDLIST\n")), "application/vnd.apple.mpegurl", CancellationToken.None);
        await storage.PutAsync(VideoFiles.Key(tenantId, s.Course.Id, id, "seg00001.ts"), new MemoryStream([0x47, 9]), "video/mp2t", CancellationToken.None);

        var link = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"));
        Assert.Contains("/hls/index.m3u8?token=", link.GetProperty("url").GetString());
        var anonymous = Anonymous(s);
        var playlist = await (await anonymous.GetAsync(link.GetProperty("url").GetString())).Content.ReadAsStringAsync();
        Assert.Contains("seg00001.ts?token=", playlist);
        var token = link.GetProperty("url").GetString()!.Split("token=")[1];
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{Videos}/{id}/hls/seg00001.ts?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/seg00002.ts?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Videos}/{id}/hls/master.m3u8?token={token}")).StatusCode);
    }

    // ---------- captions ----------
    private const string Vtt = "WEBVTT\n\n00:00:01.500 --> 00:00:04.000\nTom & Jerry say 5 > 3\n\n00:01:02.000 --> 00:01:05.250\nSecond line.";

    [Fact]
    public async Task A_transcript_becomes_a_captions_file_offered_with_the_playback_link()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"))).GetProperty("captionsUrl").ValueKind);   // none until there is a transcript

        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/transcript", new { text = Vtt, language = "en" })).StatusCode);
        var link = await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"));
        Assert.Equal("en", link.GetProperty("captionsLanguage").GetString());
        var response = await Anonymous(s).GetAsync(link.GetProperty("captionsUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/vtt", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("WEBVTT\n\n1\n00:00:01.500 --> 00:00:04.000\n", text);
        Assert.Contains("Tom &amp; Jerry say 5 &gt; 3", text);                    // words cannot break the file
        Assert.Contains("2\n00:01:02.000 --> 00:01:05.250\nSecond line.", text);
    }

    [Fact]
    public async Task Captions_need_a_ready_transcript_and_a_valid_token_and_a_language_that_is_not_a_name_becomes_undetermined()
    {
        var s = await NewSetupAsync();
        var id = await ReadyVideoAsync(s);
        var token = (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"))).GetProperty("url").GetString()!.Split("token=")[1];
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(s).GetAsync($"{Videos}/{id}/captions.vtt?token={token}")).StatusCode);          // no transcript
        await s.Teacher.Client.PostAsJsonAsync($"{Videos}/{id}/transcript", new { text = Vtt, language = "english" });
        Assert.Equal(HttpStatusCode.OK, (await Anonymous(s).GetAsync($"{Videos}/{id}/captions.vtt?token={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(s).GetAsync($"{Videos}/{id}/captions.vtt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous(s).GetAsync($"{Videos}/{id}/captions.vtt?token=forged")).StatusCode);
        Assert.Equal("und", (await ReadAsync(await s.Ada.Client.GetAsync($"{Videos}/{id}/link"))).GetProperty("captionsLanguage").GetString());
    }

    // ---------- the ladder and FFmpeg's arguments ----------
    [Theory]
    [InlineData(2160, new[] { 1080, 720, 480, 360, 240 })]
    [InlineData(1080, new[] { 1080, 720, 480, 360, 240 })]
    [InlineData(900, new[] { 720, 480, 360, 240 })]
    [InlineData(360, new[] { 360, 240 })]
    [InlineData(300, new[] { 240 })]
    [InlineData(200, new[] { 200 })]
    [InlineData(101, new[] { 100 })]
    public void Only_the_qualities_that_fit_the_video_are_made(int sourceHeight, int[] expected)
        => Assert.Equal(expected, FfmpegVideoTranscoder.ChooseHeights(sourceHeight));

    [Fact]
    public void Each_quality_gets_the_bit_rate_of_its_rung()
    {
        Assert.Equal(5000, FfmpegVideoTranscoder.KbpsFor(1080));
        Assert.Equal(1400, FfmpegVideoTranscoder.KbpsFor(480));
        Assert.Equal(400, FfmpegVideoTranscoder.KbpsFor(240));
        Assert.Equal(400, FfmpegVideoTranscoder.KbpsFor(200));
    }

    [Fact]
    public void FFmpeg_is_told_to_make_every_quality_with_sound_in_each_and_a_master_playlist()
    {
        var args = string.Join(' ', FfmpegVideoTranscoder.BuildHlsArguments([720, 360], hasAudio: true));
        Assert.Contains("[0:v:0]split=2[s0][s1];[s0]scale=-2:720[o0];[s1]scale=-2:360[o1]", args);
        Assert.Contains("-map [o0] -map [o1] -map 0:a:0 -map 0:a:0", args);
        Assert.Contains("-b:v:0 2800k", args);
        Assert.Contains("-b:v:1 800k", args);
        Assert.Contains("-var_stream_map v:0,a:0 v:1,a:1", args);
        Assert.Contains("-master_pl_name master.m3u8", args);
        Assert.EndsWith("-hls_segment_filename v%v-seg%05d.ts v%v-index.m3u8", args);

        var single = string.Join(' ', FfmpegVideoTranscoder.BuildHlsArguments([240], hasAudio: false));
        Assert.Contains("[0:v:0]scale=-2:240[o0]", single);
        Assert.DoesNotContain("split", single);
        Assert.DoesNotContain("0:a:0", single);
        Assert.DoesNotContain("-c:a", single);
        Assert.Contains("-var_stream_map v:0", single);
    }

    [Fact]
    public void The_size_sound_and_length_are_read_from_FFmpegs_description_of_the_file()
    {
        var log = "Input #0, mov,mp4, from 'input.bin':\n  Duration: 00:00:08.00, start: 0.000000, bitrate: 124 kb/s\n  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 25 fps\n  Stream #0:1[0x2](und): Audio: aac (LC), 44100 Hz, mono";
        var probe = FfmpegVideoTranscoder.ParseProbe(log)!;
        Assert.Equal((640, 360, true, 8), (probe.Width, probe.Height, probe.HasAudio, probe.DurationSeconds));
        Assert.False(FfmpegVideoTranscoder.ParseProbe(log.Replace("Audio:", "Data:"))!.HasAudio);
        Assert.Null(FfmpegVideoTranscoder.ParseProbe("Stream #0:0: Audio: mp3, 44100 Hz"));        // no picture: not a video
        Assert.Null(FfmpegVideoTranscoder.ParseProbe("garbage"));
    }

    [Fact]
    public void The_layout_is_written_and_read_back_and_requests_can_only_name_what_exists()
    {
        var renditions = new[] { new Rendition(720, 3), new Rendition(360, 3) };
        Assert.Equal("720:3,360:3", VideoFiles.FormatLayout(renditions));
        Assert.Equal(renditions, VideoFiles.ParseLayout("720:3,360:3,junk,x:y"));
        var video = new Lms.Api.Domain.Videos.Video { HlsLayout = "720:3,360:2", HlsSegmentCount = 3, HasPoster = true };
        Assert.True(VideoFiles.Exists(video, "master.m3u8"));
        Assert.True(VideoFiles.Exists(video, "v1-seg00001.ts"));
        Assert.False(VideoFiles.Exists(video, "v1-seg00002.ts"));       // the lower quality has two pieces
        Assert.True(VideoFiles.Exists(video, "v0-seg00002.ts"));
        Assert.False(VideoFiles.Exists(video, "seg00000.ts"));
        Assert.False(VideoFiles.Exists(video, "../master.m3u8"));
        Assert.Equal(1 + 2 + 5 + 1, VideoFiles.FilesOf(video).Count());   // master, two playlists, five pieces, the poster
    }

    [Fact]
    public void The_duration_is_read_from_FFmpegs_log()
    {
        Assert.Equal(8, FfmpegVideoTranscoder.ParseDuration("  Duration: 00:00:08.00, start: 0.000000, bitrate: 124 kb/s"));
        Assert.Equal(3723, FfmpegVideoTranscoder.ParseDuration("Duration: 01:02:02.50, start"));
        Assert.Null(FfmpegVideoTranscoder.ParseDuration("no duration here"));
    }
}
