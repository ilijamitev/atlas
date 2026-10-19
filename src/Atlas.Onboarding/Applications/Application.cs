using Atlas.Shared.Contracts;
using Atlas.Shared.Markets;

namespace Atlas.Onboarding.Applications;

/// <summary>Where an application is. Customers see a coarser, neutral status (<see cref="CustomerStatus"/>).</summary>
public enum ApplicationState
{
    Draft,

    /// <summary>Waiting for IDNow (the worker).</summary>
    Verifying,

    /// <summary>Waiting for Screening and World-Check (the worker).</summary>
    Screening,

    /// <summary>A possible match waits for a compliance officer; the worker checks Screening for the decision.</summary>
    UnderReview,

    /// <summary>The applicant has to retake a photo.</summary>
    ActionRequired,

    /// <summary>Screening cleared, and the market requires a branch visit before activation (Annex B, MD).</summary>
    AwaitingBranchVisit,

    /// <summary>Opening the account and ordering the card (the worker).</summary>
    OpeningAccount,

    Approved,
    Rejected,
}

public static class RejectionReasons
{
    public const string DocumentInvalid = "document.invalid";
    public const string IdentityNotVerified = "identity.notVerified";
    public const string ScreeningRejected = "screening.rejected";
}

/// <summary>What IDNow said about one identification attempt.</summary>
public sealed record IdentificationResult(string IdentificationId, string DocumentResult, bool FaceMatch, double Confidence)
{
    public bool Passes(double threshold) => DocumentResult == "VALID" && FaceMatch && Confidence >= threshold;
}

public sealed class InvalidTransitionException(ApplicationState state, string action)
    : InvalidOperationException($"Can't {action} an application that is {state}.");

/// <summary>
/// One onboarding application and its state machine. Every transition is a method here, with no I/O: the endpoints
/// and the worker load the application, call one method, and save. Technical failures never change the state; they
/// park the application with a backoff (<see cref="Park"/>).
/// </summary>
public sealed class Application
{
    /// <summary>Ten years from submission (GC-2026-0814 #4; "date of application" read as submission).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(3653);

    public Guid Id { get; set; }
    public ApplicationState State { get; private set; }
    public string IdempotencyKey { get; set; } = "";

    /// <summary>SHA-256 of the create request's body, so a reused key with a different body is refused.</summary>
    public byte[] RequestHash { get; set; } = [];

    /// <summary>SHA-256 of the applicant's token. The token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset? RetentionUntil { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>When the worker should next pick this up; null while the application waits on a person or is final.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; set; }
    public int RetryCount { get; private set; }
    public int ReviewChecks { get; private set; }

    /// <summary>A short technical reason for the last failure. Never personal data.</summary>
    public string? LastError { get; private set; }

    /// <summary>A permanent failure stopped the retries; operations have to look at it.</summary>
    public bool NeedsAttention { get; private set; }

    public int RetakeCount { get; private set; }
    public DateTimeOffset? RetakeRequestedAt { get; private set; }
    public string? AccountReference { get; private set; }
    public string? CardReference { get; private set; }
    public DateTimeOffset? BranchActivatedAt { get; private set; }
    public string? BranchActivatedBy { get; private set; }

    /// <summary>Erasure of a submitted application: processing stops, deletion waits for the end of retention (GC #5).</summary>
    public DateTimeOffset? RestrictedAt { get; private set; }

    public byte[] RowVersion { get; set; } = [];

    public Applicant Applicant { get; set; } = null!;
    public List<Document> Documents { get; set; } = [];
    public List<StatusChange> History { get; set; } = [];
    public List<ProviderResult> ProviderResults { get; set; } = [];

    public bool IsSubmitted => SubmittedAt is not null;
    public bool IsRestricted => RestrictedAt is not null;
    public bool IsFinal => State is ApplicationState.Approved or ApplicationState.Rejected;

    /// <summary>The worker's states: something happens next without anyone acting.</summary>
    public bool IsInProgress => State is ApplicationState.Verifying or ApplicationState.Screening or ApplicationState.OpeningAccount;

    public Document? CurrentIdentityDocument => Current(DocumentTypes.IsIdentity);

    public Document? CurrentSelfie => Current(type => type == DocumentTypes.Selfie);

    public static Application Start(Applicant applicant, string idempotencyKey, byte[] requestHash, byte[] tokenHash, DateTimeOffset now)
    {
        var application = new Application
        {
            State = ApplicationState.Draft,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            TokenHash = tokenHash,
            CreatedAt = now,
            UpdatedAt = now,
            Applicant = applicant,
        };
        application.History.Add(new StatusChange { From = null, To = ApplicationState.Draft, At = now });
        return application;
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    /// <summary>Submits a complete draft, or resubmits after a retake. Retention starts at the first submission.</summary>
    public void Submit(DateTimeOffset now)
    {
        EnsureNotRestricted("submit");
        switch (State)
        {
            case ApplicationState.Draft:
                SubmittedAt = now;
                RetentionUntil = now + Retention;
                break;
            case ApplicationState.ActionRequired:
                RetakeCount++;
                RetakeRequestedAt = null;
                break;
            default:
                throw new InvalidTransitionException(State, "submit");
        }

        Succeeded();
        MoveTo(ApplicationState.Verifying, now);
        DueNow(now);
    }

    /// <summary>
    /// An invalid document rejects. A valid one with a matching face and enough confidence goes on to screening, so a
    /// forged document never reaches World-Check or an officer. Anything else asks for a retake while the market's
    /// retakes last, then rejects.
    /// </summary>
    public void RecordIdentification(IdentificationResult result, MarketOptions market, DateTimeOffset now)
    {
        Expect(ApplicationState.Verifying, "record an identification for");
        Succeeded();

        if (result.DocumentResult == "INVALID")
        {
            Reject(RejectionReasons.DocumentInvalid, now);
        }
        else if (result.Passes(market.ConfidenceThreshold))
        {
            MoveTo(ApplicationState.Screening, now);
            DueNow(now);
        }
        else if (RetakeCount < market.MaxRetakes)
        {
            MoveTo(ApplicationState.ActionRequired, now, RetakeReason(result, market));
            RetakeRequestedAt = now;
            Idle();
        }
        else
        {
            Reject(RejectionReasons.IdentityNotVerified, now);
        }
    }

    /// <summary>
    /// Screening's answer, the first time or on a later check. Referred waits for an officer, checked again on the
    /// schedule; only Screening can say cleared or rejected, and only an officer makes it say so after a referral.
    /// </summary>
    public void RecordScreening(string status, MarketOptions market, ReviewSchedule schedule, DateTimeOffset now)
    {
        if (IsRestricted || State is not (ApplicationState.Screening or ApplicationState.UnderReview))
        {
            throw new InvalidTransitionException(State, "record screening for");
        }

        Succeeded();
        switch (status)
        {
            case ScreeningStatus.Cleared:
                Cleared(market, now);
                break;
            case ScreeningStatus.Rejected:
                Reject(RejectionReasons.ScreeningRejected, now);
                break;
            case ScreeningStatus.Referred when State == ApplicationState.Screening:
                MoveTo(ApplicationState.UnderReview, now);
                ReviewChecks = 0;
                NextAttemptAt = now + schedule.Interval(0);
                break;
            case ScreeningStatus.Referred:
                ReviewChecks++;
                NextAttemptAt = now + schedule.Interval(ReviewChecks);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown screening status.");
        }
    }

    /// <summary>Branch staff checked the applicant's ID in person and collected the wet signature (Annex B, MD).</summary>
    public void ActivateInBranch(string staff, DateTimeOffset now)
    {
        Expect(ApplicationState.AwaitingBranchVisit, "activate");
        BranchActivatedAt = now;
        BranchActivatedBy = staff;
        MoveTo(ApplicationState.OpeningAccount, now, "branch.activated");
        DueNow(now);
    }

    public void RecordAccount(string reference)
    {
        Expect(ApplicationState.OpeningAccount, "record an account for");
        AccountReference = reference;
    }

    /// <summary>The card is the last step: APPROVED means both core-banking calls succeeded (AC6).</summary>
    public void RecordCard(string reference, DateTimeOffset now)
    {
        Expect(ApplicationState.OpeningAccount, "record a card for");
        if (AccountReference is null)
        {
            throw new InvalidOperationException("The card is ordered after the account is open.");
        }

        CardReference = reference;
        Succeeded();
        MoveTo(ApplicationState.Approved, now);
        DecidedAt = now;
        Idle();
    }

    /// <summary>A technical failure: same state, try again later. Nobody is rejected for an outage.</summary>
    public void Park(string error, TimeSpan delay, DateTimeOffset now)
    {
        RetryCount++;
        LastError = error;
        NextAttemptAt = now + delay;
        LeaseUntil = null;
        UpdatedAt = now;
    }

    /// <summary>A permanent failure: same state, no more retries, and operations get the case.</summary>
    public void FlagForOperations(string error, DateTimeOffset now)
    {
        NeedsAttention = true;
        LastError = error;
        NextAttemptAt = null;
        LeaseUntil = null;
        UpdatedAt = now;
    }

    public void Restrict(DateTimeOffset now)
    {
        if (!IsSubmitted)
        {
            throw new InvalidOperationException("A draft is deleted on erasure, not restricted.");
        }

        RestrictedAt ??= now;
        NextAttemptAt = null;
        LeaseUntil = null;
        UpdatedAt = now;
    }

    public void RecordProviderResult(string provider, string? reference, string outcome, DateTimeOffset now) =>
        ProviderResults.Add(new ProviderResult { Provider = provider, Reference = reference, Outcome = outcome, At = now });

    /// <summary>A retake needs at least one photo uploaded after the retake was asked for.</summary>
    public bool HasPhotoSinceRetakeRequest() =>
        RetakeRequestedAt is { } requested && Documents.Any(d => d.State == DocumentState.Stored && d.CreatedAt >= requested);

    private void Cleared(MarketOptions market, DateTimeOffset now)
    {
        if (market.Activation == ActivationMode.InBranch)
        {
            MoveTo(ApplicationState.AwaitingBranchVisit, now);
            Idle();
        }
        else
        {
            MoveTo(ApplicationState.OpeningAccount, now);
            DueNow(now);
        }
    }

    private void Reject(string reason, DateTimeOffset now)
    {
        RejectionReason = reason;
        MoveTo(ApplicationState.Rejected, now, reason);
        DecidedAt = now;
        Idle();
    }

    private static string RetakeReason(IdentificationResult result, MarketOptions market) =>
        result.DocumentResult == "INCONCLUSIVE" ? "identity.inconclusive"
        : !result.FaceMatch ? "identity.faceMismatch"
        : result.Confidence < market.ConfidenceThreshold ? "identity.lowConfidence"
        : "identity.retake";

    private Document? Current(Func<string, bool> type) =>
        Documents.Where(d => d.State == DocumentState.Stored && type(d.Type)).MaxBy(d => d.CreatedAt);

    private void MoveTo(ApplicationState next, DateTimeOffset now, string? reason = null)
    {
        History.Add(new StatusChange { From = State, To = next, At = now, Reason = reason });
        State = next;
        UpdatedAt = now;
    }

    private void DueNow(DateTimeOffset now) => NextAttemptAt = now;

    private void Idle()
    {
        NextAttemptAt = null;
        LeaseUntil = null;
    }

    private void Succeeded()
    {
        RetryCount = 0;
        LastError = null;
    }

    private void Expect(ApplicationState expected, string action)
    {
        EnsureNotRestricted(action);
        if (State != expected)
        {
            throw new InvalidTransitionException(State, action);
        }
    }

    private void EnsureNotRestricted(string action)
    {
        if (IsRestricted)
        {
            throw new InvalidTransitionException(State, $"{action} (restricted)");
        }
    }
}

/// <summary>One state change, with a reason code where there is one. No personal data.</summary>
public sealed class StatusChange
{
    public long Id { get; set; }
    public Guid ApplicationId { get; set; }
    public ApplicationState? From { get; set; }
    public ApplicationState To { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A provider's answer: its reference and outcome, never the images or the personal data sent.</summary>
public sealed class ProviderResult
{
    public long Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string Provider { get; set; } = "";
    public string? Reference { get; set; }
    public string Outcome { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
