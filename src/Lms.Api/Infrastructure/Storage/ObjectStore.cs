using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Lms.Api.Infrastructure.Security;

namespace Lms.Api.Infrastructure.Storage;

/// <summary>
/// The few operations the LMS needs from an object store (S3, MinIO, ...). Keeping this small makes the storage
/// logic testable without a real server and keeps AWS types out of the rest of the code.
/// </summary>
public interface IObjectStore
{
    Task PutAsync(string key, Stream content, long length, string contentType, CancellationToken cancellationToken);
    /// <summary>Returns null when the object does not exist.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    /// <summary>Removes the object. Removing one that is not there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    /// <summary>A link anyone holding it can use until it expires, so media can stream straight from storage.</summary>
    Task<Uri?> CreateTemporaryUrlAsync(string key, TimeSpan lifetime, string contentType, string contentDisposition, CancellationToken cancellationToken);
    /// <summary>Throws when the store cannot be reached or the bucket is unusable. Used by the readiness check.</summary>
    Task PingAsync(CancellationToken cancellationToken);
    /// <summary>Creates the bucket if it does not exist (development convenience; never needed with a provisioned bucket).</summary>
    Task EnsureBucketAsync(CancellationToken cancellationToken);
}

public sealed class S3StorageOptions
{
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = string.Empty;
    /// <summary>Required by MinIO and most self-hosted stores (bucket in the path rather than the host name).</summary>
    public bool ForcePathStyle { get; set; }
    public string KeyPrefix { get; set; } = "lms/";
    public bool CreateBucket { get; set; }
    public int PresignSeconds { get; set; } = 3600;
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    /// <summary>Names of secrets in the managed secret store; preferred over the plain values.</summary>
    public string? AccessKeyIdReference { get; set; }
    public string? SecretAccessKeyReference { get; set; }

    public static S3StorageOptions From(IConfiguration configuration)
    {
        var options = new S3StorageOptions();
        configuration.GetSection("Storage:S3").Bind(options);
        options.PresignSeconds = Math.Clamp(options.PresignSeconds, 60, 86400);
        if (!string.IsNullOrEmpty(options.KeyPrefix) && !options.KeyPrefix.EndsWith('/')) options.KeyPrefix += "/";
        return options;
    }

    /// <summary>Returns the reasons the options cannot work, or an empty list.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Bucket)) problems.Add("Storage:S3:Bucket is required.");
        if (!string.IsNullOrWhiteSpace(ServiceUrl) && !Uri.TryCreate(ServiceUrl, UriKind.Absolute, out _)) problems.Add("Storage:S3:ServiceUrl must be an absolute URL.");
        var hasKey = !string.IsNullOrWhiteSpace(AccessKeyId) || !string.IsNullOrWhiteSpace(AccessKeyIdReference);
        var hasSecret = !string.IsNullOrWhiteSpace(SecretAccessKey) || !string.IsNullOrWhiteSpace(SecretAccessKeyReference);
        if (hasKey != hasSecret) problems.Add("Provide both an access key and a secret key, or neither to use the default AWS credential chain.");
        return problems;
    }
}

/// <summary>S3-compatible object store built on the AWS SDK. Works with AWS S3 and MinIO.</summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly S3StorageOptions options;
    private readonly bool plainHttp;

    public S3ObjectStore(S3StorageOptions options, IManagedSecretStore secrets)
    {
        this.options = options;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // S3-compatible servers (older MinIO in particular) do not understand the newer default checksum trailers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
            plainHttp = options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);

        var accessKey = Resolve(options.AccessKeyIdReference, options.AccessKeyId, secrets);
        var secretKey = Resolve(options.SecretAccessKeyReference, options.SecretAccessKey, secrets);
        client = !string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config)
            : new AmazonS3Client(config); // default chain: environment, profile, IAM role
    }

    private static string? Resolve(string? reference, string? plain, IManagedSecretStore secrets)
        => !string.IsNullOrWhiteSpace(reference) ? secrets.Get(reference) : plain;

    private string Full(string key) => options.KeyPrefix + key;

    public async Task PutAsync(string key, Stream content, long length, string contentType, CancellationToken cancellationToken)
    {
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket, Key = Full(key), InputStream = content, ContentType = contentType, AutoCloseStream = false,
            // The SDK needs a length for non-seekable streams; ours are seekable, but say so explicitly.
            Headers = { ContentLength = length }
        }, cancellationToken);
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(options.Bucket, Full(key), cancellationToken);
            return new OwnedStream(response.ResponseStream, response); // disposing the stream also releases the HTTP response
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => await client.DeleteObjectAsync(options.Bucket, Full(key), cancellationToken);

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try { await client.GetObjectMetadataAsync(options.Bucket, Full(key), cancellationToken); return true; }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public Task<Uri?> CreateTemporaryUrlAsync(string key, TimeSpan lifetime, string contentType, string contentDisposition, CancellationToken cancellationToken)
    {
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket, Key = Full(key), Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = plainHttp ? Protocol.HTTP : Protocol.HTTPS,
            // Force the type we validated on upload and decide inline vs download ourselves.
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentType = contentType, ContentDisposition = contentDisposition }
        });
        return Task.FromResult<Uri?>(new Uri(url));
    }

    public async Task PingAsync(CancellationToken cancellationToken)
        => await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket, MaxKeys = 1, Prefix = options.KeyPrefix }, cancellationToken);

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(client, options.Bucket)) return;
        await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, cancellationToken);
    }

    public void Dispose() => client.Dispose();

    /// <summary>A read-only stream that releases another resource (the S3 response) when it is disposed.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
