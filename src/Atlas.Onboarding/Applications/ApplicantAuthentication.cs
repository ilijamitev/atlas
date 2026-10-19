using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Auth;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Applications;

/// <summary>
/// The applicant's credential: 256 random bits, returned once at create. Only its SHA-256 is stored, so a database
/// copy can't be used to act as an applicant. A slow password hash isn't needed for a random token this long.
/// </summary>
internal static class ApplicationTokens
{
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool Matches(byte[] storedHash, string token) => CryptographicOperations.FixedTimeEquals(storedHash, Hash(token));
}

/// <summary>
/// Authenticates the applicant of the application in the route from its token. A missing, wrong or unknown token is
/// the same 401, so the API doesn't reveal which applications exist.
/// </summary>
internal sealed class ApplicantTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMarketDbContextFactory<OnboardingDbContext> databases,
    IMarketCatalog markets) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[ApplicantAuthentication.TokenHeader].ToString();
        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!markets.TryGet(Context.GetRouteValue("market") as string, out var market)
            || !Guid.TryParse(Context.GetRouteValue("id") as string, out var applicationId))
        {
            return AuthenticateResult.NoResult();
        }

        await using var db = databases.Create(market.Code);
        var storedHash = await db.Applications.AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => a.TokenHash)
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (storedHash is null || !ApplicationTokens.Matches(storedHash, token))
        {
            return AuthenticateResult.Fail("The application token doesn't match.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(AtlasClaims.Subject, applicationId.ToString()),
                new Claim(AtlasClaims.Role, "applicant"),
                new Claim(ApplicantAuthentication.ApplicationIdClaim, applicationId.ToString()),
            ],
            Scheme.Name,
            AtlasClaims.Subject,
            AtlasClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

internal static class ApplicantAuthenticationExtensions
{
    public static IServiceCollection AddApplicantAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApplicantTokenHandler>(ApplicantAuthentication.Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policies.Applicant, policy => policy
            .AddAuthenticationSchemes(ApplicantAuthentication.Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.Resource is HttpContext http
                && Guid.TryParse(http.GetRouteValue("id") as string, out var routeId)
                && context.User.HasClaim(ApplicantAuthentication.ApplicationIdClaim, routeId.ToString())));
        return services;
    }
}
