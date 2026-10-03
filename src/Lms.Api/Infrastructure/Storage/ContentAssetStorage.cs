using System.Security.Cryptography;

namespace Lms.Api.Infrastructure.Storage;

public sealed record StoredAsset(
    string StorageKey,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256);

public interface IContentAssetStorage
{
    Task<StoredAsset> SaveAsync(Guid tenantId, Guid courseId, IFormFile file, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes the stored file. A file that is already gone is not an error.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stores a file that was made from an upload (a streaming segment, a poster) under a key chosen by the caller.</summary>
    Task PutAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// A time-limited link the browser can use directly (for streaming media). Stores that cannot offer one
    /// return null and callers stream the file through the API instead.
    /// </summary>
    Task<Uri?> CreateTemporaryUrlAsync(string storageKey, string contentType, string fileName, bool inline, CancellationToken cancellationToken) => Task.FromResult<Uri?>(null);
}

public sealed class LocalContentAssetStorage : IContentAssetStorage
{
    private readonly string rootDirectory;

    public LocalContentAssetStorage(IConfiguration configuration)
    {
        rootDirectory = configuration["Storage:LocalRoot"]?.Trim() is { Length: > 0 } configuredRoot
            ? Path.GetFullPath(configuredRoot)
            : Path.Combine(AppContext.BaseDirectory, "content-assets");
        Directory.CreateDirectory(rootDirectory);
    }

    public async Task<StoredAsset> SaveAsync(Guid tenantId, Guid courseId, IFormFile file, CancellationToken cancellationToken)
    {
        var assetId = Guid.NewGuid();
        var extension = Path.GetExtension(file.FileName);
        if (extension.Length > 20) extension = string.Empty;
        var storageKey = $"{tenantId:D}/{courseId:D}/{assetId:D}{extension.ToLowerInvariant()}";
        var absolutePath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using (var target = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await file.CopyToAsync(target, cancellationToken);
        }

        await using var source = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(source, cancellationToken);
        return new StoredAsset(
            storageKey,
            Path.GetFileName(file.FileName),
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    public async Task PutAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try { var path = ResolvePath(storageKey); if (File.Exists(path)) File.Delete(path); } // File.Delete throws when even the folder is missing
        catch (InvalidOperationException) { /* a key outside the storage root is never touched */ }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
    {
        try { return Task.FromResult(File.Exists(ResolvePath(storageKey))); }
        catch (InvalidOperationException) { return Task.FromResult(false); }
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult<Stream?>(File.OpenRead(ResolvePath(storageKey)));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    private string ResolvePath(string storageKey)
    {
        var normalized = storageKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, normalized));
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The asset storage key is outside the configured storage root.");
        return candidate;
    }
}
