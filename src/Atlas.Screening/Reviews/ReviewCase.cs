using Atlas.Screening.Screenings;
using Atlas.Shared.Contracts;

namespace Atlas.Screening.Reviews;

public static class Decisions
{
    public const string Clear = "CLEAR";
    public const string Reject = "REJECT";
}

/// <summary>
/// A possible match waiting for a compliance officer of the market. It stays open until one decides it: an overdue
/// case is never decided automatically (GC-2026-0814 #3).
/// </summary>
public sealed class ReviewCase
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public string? Decision { get; set; }
    public string? Reason { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public ScreeningRecord Screening { get; set; } = null!;
    public byte[] RowVersion { get; set; } = [];

    public bool IsOpen => Decision is null;

    /// <summary>The officer decides the outcome, not just whether the match is real: a confirmed PEP may still be cleared.</summary>
    public void Decide(string decision, string reason, string officer, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The case is already decided.");
        }

        Decision = decision;
        Reason = reason;
        DecidedBy = officer;
        DecidedAt = now;
        Screening.Status = decision == Decisions.Clear ? ScreeningStatus.Cleared : ScreeningStatus.Rejected;
    }
}
