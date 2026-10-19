using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Atlas.Shared.Auth;

public sealed class AuthOptions
{
    public string Issuer { get; set; } = "atlas-dev";
    public string Audience { get; set; } = "atlas";

    /// <summary>Base64, at least 32 bytes. A shared development key; production uses the bank's identity provider.</summary>
    public string SigningKey { get; set; } = "";

    internal SymmetricSecurityKey SecurityKey() => new(Convert.FromBase64String(SigningKey));
}

public static class Roles
{
    public const string Officer = "officer";
    public const string BranchStaff = "branch";
    public const string Service = "service";
}

public static class Policies
{
    /// <summary>A compliance officer of the market in the route (GC-2026-0814 #3).</summary>
    public const string Officer = "officer";

    /// <summary>Branch staff of the market in the route (Annex B, MD activation).</summary>
    public const string BranchStaff = "branch-staff";

    /// <summary>Another Atlas service.</summary>
    public const string Service = "service";

    /// <summary>The applicant, holding the token of the application in the route.</summary>
    public const string Applicant = "applicant";
}

/// <summary>How applicants authenticate: the token issued with their application, in a header.</summary>
public static class ApplicantAuthentication
{
    public const string Scheme = "ApplicationToken";
    public const string TokenHeader = "X-Application-Token";
    public const string ApplicationIdClaim = "application_id";
}

public static class AtlasClaims
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string Market = "market";
}

public static class AtlasAuthExtensions
{
    public static IServiceCollection AddAtlasAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection("Atlas:Auth"))
            .Validate(HasUsableKey, "Atlas:Auth:SigningKey must be a base64 key of at least 32 bytes.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DevTokenIssuer>();
        services.AddSingleton<ServiceTokens>();
        services.AddTransient<ServiceTokenHandler>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = auth.Value.Issuer,
                    ValidAudience = auth.Value.Audience,
                    IssuerSigningKey = auth.Value.SecurityKey(),
                    NameClaimType = AtlasClaims.Subject,
                    RoleClaimType = AtlasClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddSingleton<IAuthorizationHandler, SameMarketHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Officer, p => p.RequireRole(Roles.Officer).AddRequirements(new SameMarketRequirement()))
            .AddPolicy(Policies.BranchStaff, p => p.RequireRole(Roles.BranchStaff).AddRequirements(new SameMarketRequirement()))
            .AddPolicy(Policies.Service, p => p.RequireRole(Roles.Service));
        return services;
    }

    private static bool HasUsableKey(AuthOptions options)
    {
        try
        {
            return Convert.FromBase64String(options.SigningKey).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The token's market claim must match the market in the route: an MA officer gets 403 on an MB case.</summary>
internal sealed class SameMarketRequirement : IAuthorizationRequirement;

internal sealed class SameMarketHandler : AuthorizationHandler<SameMarketRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SameMarketRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && http.GetRouteValue("market") is string market
            && string.Equals(context.User.FindFirst(AtlasClaims.Market)?.Value, market, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
