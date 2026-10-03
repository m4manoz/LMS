using System.Collections.Concurrent;
using Lms.Api.Infrastructure.Storage;

namespace Lms.Api.Tests;

/// <summary>A fake object store for tests: keeps objects in memory and records what the code asked for.</summary>
public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> objects = new();

    public bool Failing { get; set; }
    public bool BucketEnsured { get; private set; }
    public string? LastDisposition { get; private set; }
    public string? LastContentType { get; private set; }
    public TimeSpan? LastLifetime { get; private set; }

    public IReadOnlyCollection<string> Keys => objects.Keys.ToList();
    public byte[]? Bytes(string key) => objects.TryGetValue(key, out var item) ? item.Bytes : null;
    public string? ContentTypeOf(string key) => objects.TryGetValue(key, out var item) ? item.ContentType : null;

    public async Task PutAsync(string key, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        objects[key] = (buffer.ToArray(), contentType);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult<Stream?>(objects.TryGetValue(key, out var item) ? new MemoryStream(item.Bytes) : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(objects.ContainsKey(key));
    }

    public Task<Uri?> CreateTemporaryUrlAsync(string key, TimeSpan lifetime, string contentType, string contentDisposition, CancellationToken cancellationToken)
    {
        LastLifetime = lifetime; LastContentType = contentType; LastDisposition = contentDisposition;
        return Task.FromResult<Uri?>(new Uri($"https://objects.test/{key}?expires={(long)lifetime.TotalSeconds}"));
    }

    public Task PingAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.CompletedTask;
    }

    public Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        BucketEnsured = true;
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failing) throw new IOException("The object store is unreachable.");
    }
}
