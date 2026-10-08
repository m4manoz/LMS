using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;

namespace Lms.Api.Infrastructure.Storage;

/// <summary>A file was refused (it looks harmful, or it could not be checked). The message is safe to show to the person who uploaded it.</summary>
public sealed class FileRejectedException(string message) : Exception(message);

/// <summary>The scanner could not give an answer (it is down, timed out, or refused the file).</summary>
public sealed class ScanUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ScanResult(bool Clean, string? Threat = null);

public interface IFileScanner
{
    /// <summary>Checks the file's bytes. Throws <see cref="ScanUnavailableException"/> when no answer could be had.</summary>
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Used when no scanner is set up: every file passes.</summary>
public sealed class NoFileScanner : IFileScanner
{
    public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => Task.FromResult(new ScanResult(true));
}

public sealed class FileScanOptions
{
    public string Provider { get; set; } = "None";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>What to do when the scanner cannot answer: Reject (the safe default) or Allow.</summary>
    public string OnError { get; set; } = "Reject";

    public bool Enabled => Provider.Equals("ClamAv", StringComparison.OrdinalIgnoreCase);
    public bool AllowWhenUnavailable => OnError.Equals("Allow", StringComparison.OrdinalIgnoreCase);

    public static FileScanOptions From(IConfiguration configuration)
    {
        var options = new FileScanOptions();
        configuration.GetSection("Storage:VirusScan").Bind(options);
        options.TimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 1, 600);
        return options;
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!Provider.Equals("None", StringComparison.OrdinalIgnoreCase) && !Enabled) problems.Add("Storage:VirusScan:Provider must be None or ClamAv.");
        if (Enabled && string.IsNullOrWhiteSpace(Host)) problems.Add("Storage:VirusScan:Host is required for ClamAv.");
        if (Enabled && Port is < 1 or > 65535) problems.Add("Storage:VirusScan:Port must be a valid port.");
        if (!OnError.Equals("Reject", StringComparison.OrdinalIgnoreCase) && !OnError.Equals("Allow", StringComparison.OrdinalIgnoreCase)) problems.Add("Storage:VirusScan:OnError must be Reject or Allow.");
        return problems;
    }
}

/// <summary>
/// Scans with a ClamAV daemon (clamd) over its INSTREAM protocol: the file is streamed to the daemon in chunks and the daemon answers
/// "stream: OK" or "stream: Name FOUND". Nothing is written to the daemon's disk. clamd refuses streams over its StreamMaxLength (25 MB by default),
/// so raise that setting to your largest upload; a refusal is treated like any other failure to scan.
/// </summary>
public sealed class ClamAvScanner(FileScanOptions options) : IFileScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

            var buffer = new byte[ChunkSize];
            var header = new byte[4];
            int read;
            while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)read);
                await network.WriteAsync(header, timeout.Token);
                await network.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            await network.WriteAsync(new byte[4], timeout.Token);   // a zero-length chunk ends the stream
            await network.FlushAsync(timeout.Token);
            return Parse(await ReadAnswerAsync(network, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ScanUnavailableException("The virus scanner did not answer in time."); }
        catch (Exception exception) when (exception is SocketException or IOException) { throw new ScanUnavailableException("The virus scanner could not be reached.", exception); }
    }

    private static async Task<string> ReadAnswerAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 4096 && await network.ReadAsync(one, cancellationToken) == 1 && one[0] != 0) bytes.Add(one[0]);
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>"stream: OK", "stream: Eicar-Test-Signature FOUND" or "... ERROR".</summary>
    public static ScanResult Parse(string answer)
    {
        if (answer.EndsWith("OK", StringComparison.Ordinal)) return new ScanResult(true);
        if (answer.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var name = answer["stream:".Length..].Trim();
            return new ScanResult(false, name[..^"FOUND".Length].Trim());
        }
        throw new ScanUnavailableException(string.IsNullOrEmpty(answer) ? "The virus scanner gave no answer." : $"The virus scanner reported: {answer}");
    }
}

/// <summary>
/// Puts every upload through the scanner before it is stored. All uploads (course files, assignments, messages, forum posts, assessment answers, videos)
/// reach storage through <see cref="IContentAssetStorage.SaveAsync"/>, so this one place covers them all. Files the system makes itself (video pieces) are not scanned.
/// </summary>
public sealed class ScanningContentAssetStorage(IContentAssetStorage inner, IFileScanner scanner, FileScanOptions options, ILogger<ScanningContentAssetStorage> logger) : IContentAssetStorage
{
    public async Task<StoredAsset> SaveAsync(Guid tenantId, Guid courseId, IFormFile file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await scanner.ScanAsync(stream, cancellationToken);
            if (!result.Clean)
            {
                logger.LogWarning("Upload {FileName} for tenant {TenantId} was rejected by the virus scanner: {Threat}.", Path.GetFileName(file.FileName), tenantId, result.Threat);
                throw new FileRejectedException("The file was not accepted because the virus scanner found something harmful in it.");
            }
        }
        catch (ScanUnavailableException exception)
        {
            logger.LogError(exception, "The virus scanner could not check {FileName} for tenant {TenantId}.", Path.GetFileName(file.FileName), tenantId);
            if (!options.AllowWhenUnavailable) throw new FileRejectedException("The file could not be checked for viruses right now, so it was not accepted. Please try again in a moment.");
        }
        return await inner.SaveAsync(tenantId, courseId, file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken) => inner.OpenReadAsync(storageKey, cancellationToken);
    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken) => inner.ExistsAsync(storageKey, cancellationToken);
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => inner.DeleteAsync(storageKey, cancellationToken);
    public Task PutAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken) => inner.PutAsync(storageKey, content, contentType, cancellationToken);
    public Task<Uri?> CreateTemporaryUrlAsync(string storageKey, string contentType, string fileName, bool inline, CancellationToken cancellationToken)
        => inner.CreateTemporaryUrlAsync(storageKey, contentType, fileName, inline, cancellationToken);
}

/// <summary>Turns a refused upload into a plain 400 with the reason, wherever in the app the upload happened.</summary>
public sealed class FileRejectedExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not FileRejectedException) return false;
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { message = exception.Message, fileRejected = true }, cancellationToken);
        return true;
    }
}
