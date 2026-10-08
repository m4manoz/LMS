namespace Lms.Api.Infrastructure.Storage;

/// <summary>What people may attach to a message, a forum post or an assessment answer: any ordinary file up to 25 MB, but nothing a browser or the system would run.</summary>
public static class AttachmentRules
{
    public const int MaxBytes = 25 * 1024 * 1024;

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".com", ".scr", ".msi", ".dll", ".js", ".vbs", ".ps1", ".sh", ".jar", ".apk", ".app", ".lnk", ".html", ".htm", ".svg" };

    /// <summary>The reason this file cannot be attached, or null when it can.</summary>
    public static string? Problem(IFormFile? file)
    {
        if (file is null || file.Length == 0) return "Choose a file to attach.";
        if (file.Length > MaxBytes) return "Files must be 25 MB or smaller.";
        if (Blocked.Contains(Path.GetExtension(file.FileName))) return "This type of file cannot be attached. Use a document, image, archive or similar.";
        return null;
    }

    /// <summary>Reads the first uploaded file of a form post; a request that is not a form (or is broken) has none.</summary>
    public static async Task<(IFormFile? File, IFormCollection? Form)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            return (form.Files.FirstOrDefault(), form);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or InvalidOperationException) { return (null, null); }
    }
}
