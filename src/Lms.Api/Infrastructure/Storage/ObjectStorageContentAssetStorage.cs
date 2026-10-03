using System.Net.Mime;
using System.Security.Cryptography;

namespace Lms.Api.Infrastructure.Storage;

/// <summary>
/// Stores content assets in an object store. The storage key is logical (tenant/course/asset) and is the same
/// shape the local provider uses, so switching providers does not change what the database holds.
/// </summary>
public sealed class ObjectStorageContentAssetStorage(IObjectStore store, S3StorageOptions options) : IContentAssetStorage
{
    public async Task<StoredAsset> SaveAsync(Guid tenantId, Guid courseId, IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName);
        if (extension.Length > 20 || extension.Any(c => !char.IsLetterOrDigit(c) && c != '.')) extension = string.Empty;
        var key = $"{tenantId:D}/{courseId:D}/{Guid.NewGuid():D}{extension.ToLowerInvariant()}";
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

        // Hash first, then upload from the start: the uploaded bytes and the recorded checksum come from the same file.
        string hash;
        await using (var hashing = file.OpenReadStream()) hash = Convert.ToHexString(await SHA256.HashDataAsync(hashing, cancellationToken)).ToLowerInvariant();
        await using (var upload = file.OpenReadStream()) await store.PutAsync(key, upload, file.Length, contentType, cancellationToken);
        return new StoredAsset(key, Path.GetFileName(file.FileName), contentType, file.Length, hash);
    }

    public Task PutAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
        => IsSafeKey(storageKey) ? store.PutAsync(storageKey, content, content.Length, contentType, cancellationToken) : throw new InvalidOperationException("Unsafe storage key.");

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        => IsSafeKey(storageKey) ? store.GetAsync(storageKey, cancellationToken) : Task.FromResult<Stream?>(null);

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
        => IsSafeKey(storageKey) ? store.ExistsAsync(storageKey, cancellationToken) : Task.FromResult(false);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
        => IsSafeKey(storageKey) ? store.DeleteAsync(storageKey, cancellationToken) : Task.CompletedTask;

    public Task<Uri?> CreateTemporaryUrlAsync(string storageKey, string contentType, string fileName, bool inline, CancellationToken cancellationToken)
    {
        if (!IsSafeKey(storageKey)) return Task.FromResult<Uri?>(null);
        // ContentDisposition encodes unusual file names correctly, so a name cannot break out of the header.
        var disposition = new ContentDisposition { FileName = Path.GetFileName(fileName), Inline = inline }.ToString();
        return store.CreateTemporaryUrlAsync(storageKey, TimeSpan.FromSeconds(options.PresignSeconds), contentType, disposition, cancellationToken);
    }

    /// <summary>Keys come from our own database, but never let one climb out of its folder or address another prefix.</summary>
    public static bool IsSafeKey(string key)
        => !string.IsNullOrWhiteSpace(key) && !key.StartsWith('/') && !key.Contains('\\') && !key.Contains("..") && key.All(c => c >= ' ' && c != '\u007f');
}

/// <summary>
/// Writes to the primary store and reads from it first, then from a legacy store. Lets a deployment move to S3
/// without migrating files that were uploaded to local disk earlier.
/// </summary>
public sealed class FallbackContentAssetStorage(IContentAssetStorage primary, IContentAssetStorage legacy) : IContentAssetStorage
{
    public Task<StoredAsset> SaveAsync(Guid tenantId, Guid courseId, IFormFile file, CancellationToken cancellationToken)
        => primary.SaveAsync(tenantId, courseId, file, cancellationToken);

    public Task PutAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
        => primary.PutAsync(storageKey, content, contentType, cancellationToken);

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        => await primary.OpenReadAsync(storageKey, cancellationToken) ?? await legacy.OpenReadAsync(storageKey, cancellationToken);

    public async Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken)
        => await primary.ExistsAsync(storageKey, cancellationToken) || await legacy.ExistsAsync(storageKey, cancellationToken);

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        await primary.DeleteAsync(storageKey, cancellationToken);
        await legacy.DeleteAsync(storageKey, cancellationToken);
    }

    /// <summary>Only objects that really are in the primary store get a direct link; legacy ones stream through the API.</summary>
    public async Task<Uri?> CreateTemporaryUrlAsync(string storageKey, string contentType, string fileName, bool inline, CancellationToken cancellationToken)
        => await primary.ExistsAsync(storageKey, cancellationToken) ? await primary.CreateTemporaryUrlAsync(storageKey, contentType, fileName, inline, cancellationToken) : null;
}
