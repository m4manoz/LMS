using System.Security.Cryptography;
using System.Text;

namespace Lms.Api.Infrastructure.Offline;

public static class OfflinePackageCipher
{
    public static EncryptedOfflinePayload Encrypt(byte[] payload, string secret, Guid deviceId, Guid itemId, DateTimeOffset expiresAtUtc)
    {
        var keyDerivation = $"{deviceId:D}:{itemId:D}:{expiresAtUtc.UtcTicks}";
        var key = DeriveKey(secret, keyDerivation);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, payload, ciphertext, tag);
        return new EncryptedOfflinePayload(deviceId, itemId, expiresAtUtc, "AES-256-GCM", keyDerivation, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    public static byte[] Decrypt(EncryptedOfflinePayload envelope, string secret)
    {
        var key = DeriveKey(secret, envelope.KeyDerivation);
        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var payload = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, payload);
        return payload;
    }

    private static byte[] DeriveKey(string secret, string keyDerivation) => SHA256.HashData(Encoding.UTF8.GetBytes($"{secret}:{keyDerivation}"));
}

public sealed record EncryptedOfflinePayload(Guid DeviceId, Guid ItemId, DateTimeOffset ExpiresAtUtc, string Algorithm, string KeyDerivation, string NonceBase64, string CiphertextBase64, string TagBase64);
