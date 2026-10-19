using Atlas.Screening.Reviews;
using Atlas.Shared.Contracts;

namespace Atlas.Screening.Screenings;

/// <summary>
/// One application's screening: the subject exactly as screened, World-Check's answer, and the status. Only an
/// officer's decision on the review case moves a referred screening on (GC-2026-0814 #3).
/// </summary>
public sealed class ScreeningRecord
{
    public static readonly TimeSpan ReviewDue = TimeSpan.FromHours(48);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(3653);

    public Guid ApplicationId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public string Nationality { get; set; } = "";
    public string Status { get; set; } = ScreeningStatus.Cleared;
    public string WorldCheckCaseId { get; set; } = "";
    public DateTimeOffset ScreenedAt { get; set; }

    /// <summary>Ten years from submission (GC-2026-0814 #4).</summary>
    public DateTimeOffset RetentionUntil { get; set; }

    public List<ScreeningMatch> Matches { get; set; } = [];
    public ReviewCase? Case { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>A clear result clears; any possible match opens a review case, due in 48 hours. Nothing rejects here.</summary>
    public static ScreeningRecord From(Guid applicationId, ScreeningRequest subject, WorldCheckResult result, DateTimeOffset now)
    {
        var screening = new ScreeningRecord
        {
            ApplicationId = applicationId,
            FirstName = subject.FirstName,
            LastName = subject.LastName,
            DateOfBirth = subject.DateOfBirth,
            Nationality = subject.Nationality,
            WorldCheckCaseId = result.CaseId,
            ScreenedAt = now,
            RetentionUntil = subject.SubmittedAt + Retention,
            Matches = result.Matches.Select(m => new ScreeningMatch { ListType = m.ListType, Score = m.Score, Subject = m.Subject }).ToList(),
        };

        if (result.PossibleMatch)
        {
            screening.Status = ScreeningStatus.Referred;
            screening.Case = new ReviewCase { ApplicationId = applicationId, OpenedAt = now, DueAt = now + ReviewDue };
        }

        return screening;
    }
}

/// <summary>A list entry World-Check matched the subject against.</summary>
public sealed class ScreeningMatch
{
    public long Id { get; set; }
    public Guid ApplicationId { get; set; }
    public string ListType { get; set; } = "";
    public double Score { get; set; }
    public string Subject { get; set; } = "";
}

public sealed record WorldCheckMatchResult(string ListType, double Score, string Subject);

public sealed record WorldCheckResult(string CaseId, bool PossibleMatch, IReadOnlyList<WorldCheckMatchResult> Matches);
