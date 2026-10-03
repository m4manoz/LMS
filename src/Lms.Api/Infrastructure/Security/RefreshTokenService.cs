using System.Security.Cryptography;
using System.Text;

namespace Lms.Api.Infrastructure.Security;

public static class RefreshTokenService
{
    public static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .Replace("+", "-", StringComparison.Ordinal)
        .Replace("/", "_", StringComparison.Ordinal)
        .TrimEnd('=');

    public static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();
}

