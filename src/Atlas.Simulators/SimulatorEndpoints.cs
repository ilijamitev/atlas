using Atlas.Shared.Auth;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Atlas.Simulators.Control;
using Atlas.Simulators.Scenarios;

namespace Atlas.Simulators;

public sealed record FaultSettingsRequest(string? Mode, int? FailNext, int? LatencyMs);

public sealed record ProviderFaults(string Provider, string Mode, int FailNext, int? LatencyMs);

public sealed record DevTokenRequest(string? Role, string? Market, string? Subject);

public sealed record DevTokenResponse(string AccessToken, string TokenType, int ExpiresIn, string Subject, string Role, string? Market);

internal static class SimulatorEndpoints
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(8);

    public static IServiceCollection AddSimulators(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SimulatorOptions>().Bind(configuration.GetSection("Simulators"));
        services.AddSingleton<FaultControl>();
        services.AddSingleton<RequestJournal>();
        services.AddSingleton<SimulatorRunner>();
        services.AddSingleton<CoreBankingSimulator>();
        return services;
    }

    public static WebApplication MapSimulators(this WebApplication app)
    {
        // The provider routes keep the ticket's paths under a prefix per provider, so a service's base URL is the only
        // thing that changes between the simulators and the real APIs.
        var providers = app.MapGroup("").WithTags("Providers");
        providers.MapPost("/idnow/v1/identifications", (IdNowRequest request, HttpContext http, SimulatorRunner runner) =>
                runner.RunAsync(Provider.IdNow, "identification", http, () => IdNowSimulator.Answer(request)))
            .WithSummary("IDNow: document authenticity and face match");
        providers.MapPost("/refinitiv/worldcheck/v2/screen", (WorldCheckRequest request, HttpContext http, SimulatorRunner runner) =>
                runner.RunAsync(Provider.WorldCheck, "screen", http, () => WorldCheckSimulator.Answer(request)))
            .WithSummary("World-Check: sanctions and PEP screening");
        providers.MapPost("/corebanking/v1/accounts", (AccountRequest request, HttpContext http, SimulatorRunner runner, CoreBankingSimulator bank) =>
                runner.RunAsync(Provider.CoreBanking, "account", http, () => bank.OpenAccount(request)))
            .WithSummary("Core banking: open the account (idempotent by application ID)");
        providers.MapPost("/corebanking/v1/cards", (CardRequest request, HttpContext http, SimulatorRunner runner, CoreBankingSimulator bank) =>
                runner.RunAsync(Provider.CoreBanking, "card", http, () => bank.OrderCard(request)))
            .WithSummary("Core banking: order the card (idempotent by application ID)");

        var control = app.MapGroup("/_control").WithTags("Control");
        control.MapGet("/", (FaultControl faults) => Enum.GetValues<Provider>().Select(p => Describe(p, faults.Get(p))))
            .WithSummary("Current fault settings of every provider");
        control.MapPut("/{provider}", SetFaults)
            .WithSummary("Set a provider's faults: mode healthy | down | error500 | slow | timeout | reject, failNext, latencyMs");
        control.MapDelete("/", (FaultControl faults, RequestJournal journal) =>
            {
                faults.Reset();
                journal.Clear();
                return TypedResults.NoContent();
            })
            .WithSummary("Make every provider healthy again and clear the journal");
        control.MapGet("/requests", (string? provider, string? keyPrefix, RequestJournal journal) => journal.Find(provider, keyPrefix))
            .WithSummary("The calls each provider received, filtered by provider and idempotency-key prefix");
        control.MapDelete("/requests", (RequestJournal journal) =>
            {
                journal.Clear();
                return TypedResults.NoContent();
            })
            .WithSummary("Clear the journal");

        app.MapPost("/dev/token", IssueToken)
            .WithTags("Dev tokens")
            .WithSummary("A development JWT for an officer, branch staff (with a market) or a service");
        return app;
    }

    private static IResult SetFaults(string provider, FaultSettingsRequest request, FaultControl faults)
    {
        var errors = new List<FieldError>();
        if (!ProviderNames.TryParse(provider, out var target))
        {
            return Problems.Of(StatusCodes.Status404NotFound, "provider.unknown", "Providers are idnow, worldcheck and corebanking.");
        }

        if (!Enum.TryParse<FaultMode>(request.Mode ?? nameof(FaultMode.Healthy), ignoreCase: true, out var mode) || int.TryParse(request.Mode, out _))
        {
            errors.Add(new FieldError("mode", "mode.unknown", "Use healthy, down, error500, slow, timeout or reject."));
        }

        if (request.FailNext is < 0 or > 1000)
        {
            errors.Add(new FieldError("failNext", "failNext.range", "failNext must be between 0 and 1000."));
        }

        if (request.LatencyMs is < 0 or > 120_000)
        {
            errors.Add(new FieldError("latencyMs", "latencyMs.range", "latencyMs must be between 0 and 120000."));
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var settings = new FaultSettings(mode, request.FailNext ?? 0, request.LatencyMs);
        faults.Set(target, settings);
        return TypedResults.Ok(Describe(target, settings));
    }

    private static IResult IssueToken(DevTokenRequest request, DevTokenIssuer issuer, IMarketCatalog markets)
    {
        var role = request.Role?.ToLowerInvariant();
        if (role is not (Roles.Officer or Roles.BranchStaff or Roles.Service))
        {
            return Problems.Validation([new FieldError("role", "role.unknown", "Use officer, branch or service.")]);
        }

        string? market = null;
        if (role is Roles.Officer or Roles.BranchStaff)
        {
            if (!markets.TryGet(request.Market, out var known))
            {
                return Problems.Validation([new FieldError("market", "market.unknown", "Officers and branch staff need a market this deployment serves.")]);
            }

            market = known.Code;
        }

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? $"{role}-{market?.ToLowerInvariant() ?? "atlas"}-1" : request.Subject.Trim();
        var token = issuer.Issue(subject, role, market, TokenLifetime);
        return TypedResults.Ok(new DevTokenResponse(token, "Bearer", (int)TokenLifetime.TotalSeconds, subject, role, market));
    }

    private static ProviderFaults Describe(Provider provider, FaultSettings settings) =>
        new(provider.Name(), settings.Mode.ToString().ToLowerInvariant(), settings.FailNext, settings.LatencyMs);
}
