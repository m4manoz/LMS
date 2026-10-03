using System.Security.Cryptography;
using System.Text;
using Lms.Api.Infrastructure.Offline;
using Xunit;

namespace Lms.Api.Tests;

public sealed class OfflinePackageCipherTests
{
    [Fact]
    public void EncryptAndDecryptRoundTripUsesAes256Gcm()
    {
        var deviceId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var payload = Encoding.UTF8.GetBytes("encrypted course package");

        var envelope = OfflinePackageCipher.Encrypt(payload, "device-secret", deviceId, itemId, expiresAt);
        var decrypted = OfflinePackageCipher.Decrypt(envelope, "device-secret");

        Assert.Equal(payload, decrypted);
        Assert.Equal("AES-256-GCM", envelope.Algorithm);
        Assert.Equal($"{deviceId:D}:{itemId:D}:{expiresAt.UtcTicks}", envelope.KeyDerivation);
    }

    [Fact]
    public void WrongSecretCannotDecryptPackage()
    {
        var envelope = OfflinePackageCipher.Encrypt([1, 2, 3], "correct-secret", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));

        Assert.ThrowsAny<CryptographicException>(() => OfflinePackageCipher.Decrypt(envelope, "wrong-secret"));
    }
}
