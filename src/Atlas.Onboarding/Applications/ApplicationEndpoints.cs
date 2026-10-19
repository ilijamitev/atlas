using System.Security.Claims;
using System.Threading.RateLimiting;
using Atlas.Shared.Auth;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Applications;

internal static class ApplicationEndpoints
{
    public const string CreateRateLimit = "create";

    public static WebApplication MapApplications(this WebApplication app)
    {
        var applications = app.MapGroup("/v1/markets/{market}/applications").WithTags("Applications").RequireKnownMarket();

        applications.MapPost("/", (string market, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpRequest request, ApplicationService service, CancellationToken ct) =>
                service.CreateAsync(market, idempotencyKey, request, ct))
            .AllowAnonymous()
            .RequireRateLimiting(CreateRateLimit)
            .Accepts<ApplicationInput>("application/json")
            .Produces<ApplicationCreatedResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Start an application with whatever the app has. A complete body is submitted at once and waits up to 8 s for the decision.");

        applications.MapGet("/{id:guid}", (string market, Guid id, ApplicationService service, CancellationToken ct) =>
                service.GetAsync(market, id, ct))
            .RequireAuthorization(Policies.Applicant)
            .Produces<ApplicationDetailsResponse>()
            .WithSummary("The status, what to do next, and the details so far (identifier masked, no images). Logged.");

        applications.MapPatch("/{id:guid}", (string market, Guid id, HttpRequest request, ApplicationService service, CancellationToken ct) =>
                service.PatchAsync(market, id, request, ct))
            .RequireAuthorization(Policies.Applicant)
            .Accepts<ApplicationInput>("application/json")
            .Produces<ApplicationStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Add or change details and photos on a draft; after a retake request, new photos only.");

        applications.MapPost("/{id:guid}/submit", (string market, Guid id, ApplicationService service, CancellationToken ct) =>
                service.SubmitAsync(market, id, ct))
            .RequireAuthorization(Policies.Applicant)
            .Produces<ApplicationStatusResponse>()
            .Produces<ApplicationStatusResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .WithSummary("Submit, then wait up to 8 s: 200 with a final answer, or 202 with the current status. Safe to repeat.");

        applications.MapPost("/{id:guid}/erasure", (string market, Guid id, ApplicationService service, CancellationToken ct) =>
                service.EraseAsync(market, id, ct))
            .RequireAuthorization(Policies.Applicant)
            .Produces<ErasureResponse>()
            .Produces<ErasureResponse>(StatusCodes.Status202Accepted)
            .WithSummary("Erasure (GC #5): a draft is deleted at once; a submitted application is restricted until retention ends.");

        var branch = app.MapGroup("/v1/markets/{market}/branch/applications")
            .WithTags("Branch activation (MD)")
            .RequireAuthorization(Policies.BranchStaff)
            .RequireKnownMarket();

        branch.MapGet("/{id:guid}", (string market, Guid id, ClaimsPrincipal user, ApplicationService service, CancellationToken ct) =>
                service.BranchViewAsync(market, id, user, ct))
            .Produces<ApplicationDetailsResponse>()
            .WithSummary("The applicant's details, to check against their ID in person. Logged.");

        branch.MapPost("/{id:guid}/activation", (string market, Guid id, BranchActivationRequest request, ClaimsPrincipal user, ApplicationService service, CancellationToken ct) =>
                service.ActivateAsync(market, id, request, user, ct))
            .Produces<ApplicationStatusResponse>()
            .Produces<ApplicationStatusResponse>(StatusCodes.Status202Accepted)
            .WithSummary("Confirm the in-person ID check and the wet signature; the account is then opened and the card ordered.");

        return app;
    }

    /// <summary>Create is the one anonymous endpoint, so it's limited per client address.</summary>
    public static IServiceCollection AddCreateRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) => new ValueTask(
                Problems.Of(StatusCodes.Status429TooManyRequests, "rate.limited", "Too many new applications from this address. Try again shortly.")
                    .ExecuteAsync(context.HttpContext));
            limiter.AddPolicy(CreateRateLimit, http =>
            {
                var options = http.RequestServices.GetRequiredService<IOptions<OnboardingOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = options.CreatePermitLimit, Window = options.CreateWindow, QueueLimit = 0 });
            });
        });
}
