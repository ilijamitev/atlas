using Atlas.Screening.Persistence;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Auth;
using Atlas.Shared.Contracts;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Screening.Screenings;

/// <summary>Onboarding's way in: screen an applicant once, then ask for the status until an officer has decided.</summary>
internal static class ScreeningEndpoints
{
    public static WebApplication MapScreenings(this WebApplication app)
    {
        var screenings = app.MapGroup("/internal/v1/markets/{market}/screenings")
            .WithTags("Screenings (internal)")
            .RequireAuthorization(Policies.Service)
            .RequireKnownMarket();

        screenings.MapPut("/{applicationId:guid}", ScreenAsync)
            .WithSummary("Screen an applicant against sanctions and PEP lists. Idempotent: a repeat returns the first result.");
        screenings.MapGet("/{applicationId:guid}", GetAsync)
            .WithSummary("A screening's status: CLEARED, REFERRED (waiting for an officer) or REJECTED");
        return app;
    }

    private static async Task<IResult> ScreenAsync(
        string market,
        Guid applicationId,
        ScreeningRequest request,
        HttpContext http,
        IMarketDbContextFactory<ScreeningDbContext> databases,
        WorldCheckClient worldCheck,
        TimeProvider time,
        ILogger<ScreeningApi> logger,
        CancellationToken cancellationToken)
    {
        if (Validate(request, time.GetUtcNow()) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        await using var db = databases.Create(market);
        if (await StatusAsync(db, applicationId, cancellationToken) is { } existing)
        {
            return TypedResults.Ok(new ScreeningResponse(applicationId, existing));
        }

        WorldCheckResult result;
        try
        {
            result = await worldCheck.ScreenAsync(applicationId, request, cancellationToken);
        }
        catch (TransientFailureException ex)
        {
            logger.LogWarning("World-Check unavailable for application {ApplicationId}: {Reason}", applicationId, ex.Reason);
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "worldcheck.unavailable", "World-Check is unavailable. Try again later.");
        }
        catch (PermanentFailureException ex)
        {
            logger.LogError("World-Check refused application {ApplicationId}: {Reason}", applicationId, ex.Reason);
            return Problems.Of(StatusCodes.Status422UnprocessableEntity, "worldcheck.refused", "World-Check refused the screening request.");
        }

        var now = time.GetUtcNow();
        var screening = ScreeningRecord.From(applicationId, request, result, now);
        db.Screenings.Add(screening);
        db.LogAccess(Actor.From(http.User), AccessAction.Create, "screening", applicationId.ToString(), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another replica screened the same application a moment earlier; its result stands.
            await using var fresh = databases.Create(market);
            var winner = await StatusAsync(fresh, applicationId, cancellationToken);
            return TypedResults.Ok(new ScreeningResponse(applicationId, winner ?? screening.Status));
        }

        logger.LogInformation(
            "Screened application {ApplicationId} in {Market}: {Status}, {MatchCount} possible matches",
            applicationId, market.ToUpperInvariant(), screening.Status, screening.Matches.Count);
        return TypedResults.Ok(new ScreeningResponse(applicationId, screening.Status));
    }

    /// <summary>
    /// The status only, so the call isn't an access-log entry: Onboarding asks once a minute or so for every open case,
    /// and none of it is personal data.
    /// </summary>
    private static async Task<IResult> GetAsync(
        string market,
        Guid applicationId,
        IMarketDbContextFactory<ScreeningDbContext> databases,
        CancellationToken cancellationToken)
    {
        await using var db = databases.Create(market);
        return await StatusAsync(db, applicationId, cancellationToken) is { } status
            ? TypedResults.Ok(new ScreeningResponse(applicationId, status))
            : Problems.Of(StatusCodes.Status404NotFound, "screening.notFound", "No screening for that application.");
    }

    private static Task<string?> StatusAsync(ScreeningDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Screenings.AsNoTracking()
            .Where(s => s.ApplicationId == applicationId)
            .Select(s => s.Status)
            .SingleOrDefaultAsync(cancellationToken);

    private static List<FieldError> Validate(ScreeningRequest request, DateTimeOffset now)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(request.FirstName) || request.FirstName.Length > 100)
        {
            errors.Add(new FieldError("firstName", "firstName.required", "A first name of up to 100 characters is required."));
        }

        if (string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Length > 100)
        {
            errors.Add(new FieldError("lastName", "lastName.required", "A last name of up to 100 characters is required."));
        }

        if (request.DateOfBirth == default || request.DateOfBirth > DateOnly.FromDateTime(now.UtcDateTime))
        {
            errors.Add(new FieldError("dateOfBirth", "dateOfBirth.invalid", "A date of birth in the past is required."));
        }

        if (request.Nationality is not { Length: 2 } nationality || !nationality.All(char.IsAsciiLetterUpper))
        {
            errors.Add(new FieldError("nationality", "nationality.format", "Nationality must be an ISO 3166 alpha-2 code."));
        }

        return errors;
    }
}
