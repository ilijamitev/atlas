using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Atlas.Shared.Auth;

/// <summary>
/// Issues development JWTs: for officers and branch staff through the simulator host's /dev/token, and for the
/// services' own calls to each other. Each token names one person or one service, so every access stays
/// attributable (GC-2026-0814 #2). Production would use the bank's identity provider and managed identities.
/// </summary>
public sealed class DevTokenIssuer(IOptions<AuthOptions> options, TimeProvider time)
{
    public string Issue(string subject, string role, string? market = null, TimeSpan? lifetime = null)
    {
        var auth = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            [AtlasClaims.Subject] = subject,
            [AtlasClaims.Role] = role,
        };
        if (market is not null)
        {
            claims[AtlasClaims.Market] = market.ToUpperInvariant();
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = auth.Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime ?? TimeSpan.FromHours(8)),
            SigningCredentials = new SigningCredentials(auth.SecurityKey(), SecurityAlgorithms.HmacSha256),
        });
    }
}
