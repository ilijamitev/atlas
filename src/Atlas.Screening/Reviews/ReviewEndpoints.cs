using Atlas.Screening.Persistence;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Auth;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Screening.Reviews;

public sealed record CaseSummary(
    Guid CaseId,
    Guid ApplicationId,
    DateTimeOffset OpenedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    IReadOnlyList<string> ListTypes,
    string? Decision);

public sealed record CaseSubject(string FirstName, string LastName, DateOnly DateOfBirth, string Nationality);

public sealed record CaseMatch(string ListType, double Score, string Subject);

public sealed record CaseDetail(
    Guid CaseId,
    Guid ApplicationId,
    DateTimeOffset OpenedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    CaseSubject Subject,
    string WorldCheckCaseId,
    IReadOnlyList<CaseMatch> Matches,
    string? Decision,
    string? Reason,
    string? DecidedBy,
    DateTimeOffset? DecidedAt);

public sealed record DecisionRequest(string? Decision, string? Reason);

/// <summary>The compliance officers' side: only an officer of the market in the route gets in (GC-2026-0814 #3).</summary>
internal static class ReviewEndpoints
{
    private const int ListLimit = 200;

    public static WebApplication MapReviews(this WebApplication app)
    {
        var cases = app.MapGroup("/v1/markets/{market}/cases")
            .WithTags("Review cases")
            .RequireAuthorization(Policies.Officer)
            .RequireKnownMarket();

        cases.MapGet("/", ListAsync)
            .WithSummary("Cases by due time, soonest first: status=open (default), decided or all. No personal data in the list.");
        cases.MapGet("/{caseId:guid}", GetAsync)
            .WithSummary("One case: the screened subject and World-Check's matches. Every read is logged.");
        cases.MapPost("/{caseId:guid}/decision", DecideAsync)
            .WithSummary("Decide a case: CLEAR or REJECT, with a reason. Logged.");
        return app;
    }

    private static async Task<IResult> ListAsync(
        string market,
        string? status,
        IMarketDbContextFactory<ScreeningDbContext> databases,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await using var db = databases.Create(market);
        var query = db.ReviewCases.AsNoTracking();
        switch (status?.ToLowerInvariant())
        {
            case null or "open":
                query = query.Where(c => c.Decision == null);
                break;
            case "decided":
                query = query.Where(c => c.Decision != null);
                break;
            case "all":
                break;
            default:
                return Problems.Validation([new FieldError("status", "status.unknown", "Use open, decided or all.")]);
        }

        var rows = await query
            .OrderBy(c => c.DueAt)
            .Take(ListLimit)
            .Select(c => new { c.Id, c.ApplicationId, c.OpenedAt, c.DueAt, c.Decision, ListTypes = c.Screening.Matches.Select(m => m.ListType).ToList() })
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        return TypedResults.Ok(rows.Select(c => new CaseSummary(
            c.Id, c.ApplicationId, c.OpenedAt, c.DueAt, c.Decision is null && c.DueAt < now, c.ListTypes.Distinct().ToList(), c.Decision)));
    }

    private static async Task<IResult> GetAsync(
        string market,
        Guid caseId,
        HttpContext http,
        IMarketDbContextFactory<ScreeningDbContext> databases,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await using var db = databases.Create(market);
        var reviewCase = await LoadAsync(db, caseId, cancellationToken);
        if (reviewCase is null)
        {
            return CaseNotFound();
        }

        // The read is logged before the data leaves; if the log can't be written, nothing is shown.
        var now = time.GetUtcNow();
        db.LogAccess(Actor.From(http.User), AccessAction.Read, "case", caseId.ToString(), now);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Detail(reviewCase, now));
    }

    private static async Task<IResult> DecideAsync(
        string market,
        Guid caseId,
        DecisionRequest request,
        HttpContext http,
        IMarketDbContextFactory<ScreeningDbContext> databases,
        TimeProvider time,
        ILogger<ScreeningApi> logger,
        CancellationToken cancellationToken)
    {
        var decision = request.Decision?.Trim().ToUpperInvariant();
        var errors = new List<FieldError>();
        if (decision is not (Decisions.Clear or Decisions.Reject))
        {
            errors.Add(new FieldError("decision", "decision.unknown", "Decide CLEAR or REJECT."));
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
        {
            errors.Add(new FieldError("reason", "reason.required", "A reason of up to 1000 characters is required."));
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        await using var db = databases.Create(market);
        var reviewCase = await LoadAsync(db, caseId, cancellationToken);
        if (reviewCase is null)
        {
            return CaseNotFound();
        }

        if (!reviewCase.IsOpen)
        {
            return AlreadyDecided();
        }

        var now = time.GetUtcNow();
        var officer = Actor.From(http.User);
        reviewCase.Decide(decision!, request.Reason!.Trim(), officer.Id, now);
        db.LogAccess(officer, AccessAction.Amend, "case", caseId.ToString(), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AlreadyDecided();
        }

        logger.LogInformation(
            "Case {CaseId} for application {ApplicationId} in {Market} decided: {Decision}",
            caseId, reviewCase.ApplicationId, market.ToUpperInvariant(), decision);
        return TypedResults.Ok(Detail(reviewCase, now));
    }

    private static Task<ReviewCase?> LoadAsync(ScreeningDbContext db, Guid caseId, CancellationToken cancellationToken) =>
        db.ReviewCases
            .Include(c => c.Screening).ThenInclude(s => s.Matches)
            .SingleOrDefaultAsync(c => c.Id == caseId, cancellationToken);

    private static CaseDetail Detail(ReviewCase c, DateTimeOffset now) => new(
        c.Id,
        c.ApplicationId,
        c.OpenedAt,
        c.DueAt,
        c.IsOpen && c.DueAt < now,
        new CaseSubject(c.Screening.FirstName, c.Screening.LastName, c.Screening.DateOfBirth, c.Screening.Nationality),
        c.Screening.WorldCheckCaseId,
        c.Screening.Matches.Select(m => new CaseMatch(m.ListType, m.Score, m.Subject)).ToList(),
        c.Decision,
        c.Reason,
        c.DecidedBy,
        c.DecidedAt);

    private static IResult CaseNotFound() =>
        Problems.Of(StatusCodes.Status404NotFound, "case.notFound", "No such case in this market.");

    private static IResult AlreadyDecided() =>
        Problems.Of(StatusCodes.Status409Conflict, "case.alreadyDecided", "The case has already been decided.");
}
