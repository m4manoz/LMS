using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lms.Api.Domain.Identity;
using Lms.Api.Domain.Tenants;
using Microsoft.IdentityModel.Tokens;

namespace Lms.Api.Infrastructure.Security;

public sealed class JwtTokenService(IConfiguration configuration)
{
    public AccessTokenResult Create(AppUser user, Tenant tenant, Role role, IEnumerable<string> permissions, Guid? sessionId = null)
        => Create(user, tenant, [role.Code], permissions, sessionId);

    public AccessTokenResult Create(AppUser user, Tenant tenant, IEnumerable<string> roleCodes, IEnumerable<string> permissions, Guid? sessionId = null)
    {
        var issuer = configuration["Auth:Issuer"] ?? throw new InvalidOperationException("Auth:Issuer is required.");
        var audience = configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required.");
        var signingKey = configuration["Auth:SigningKey"] ?? throw new InvalidOperationException("Auth:SigningKey is required.");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(configuration.GetValue("Auth:AccessTokenMinutes", 60));
        var roles = roleCodes.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new("tenant_id", tenant.Id.ToString("D")),
            new("tenant_slug", tenant.Slug)
        };
        claims.AddRange(roles.Select(roleCode => new Claim(ClaimTypes.Role, roleCode)));
        if (sessionId is Guid currentSessionId) claims.Add(new Claim("session_id", currentSessionId.ToString("D")));
        claims.AddRange(permissions.Distinct(StringComparer.OrdinalIgnoreCase).Select(permission => new Claim("permission", permission)));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, audience, claims, now.UtcDateTime, expiresAt.UtcDateTime, credentials);
        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAtUtc);
