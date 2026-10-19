using Atlas.Screening.Reviews;
using Atlas.Screening.Screenings;
using Atlas.Shared.Contracts;

namespace Atlas.UnitTests.Screening;

/// <summary>GC-2026-0814 #3 in the model: a possible match opens a case that only a decision closes.</summary>
public sealed class ScreeningRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScreeningRequest Subject = new("Test", "Tester", new DateOnly(1990, 1, 1), "XB", Now);

    [Fact]
    public void A_clear_result_clears_and_opens_no_case()
    {
        var screening = ScreeningRecord.From(Guid.NewGuid(), Subject, new WorldCheckResult("wc_1", false, []), Now);

        Assert.Equal(ScreeningStatus.Cleared, screening.Status);
        Assert.Null(screening.Case);
        Assert.Equal(Now + ScreeningRecord.Retention, screening.RetentionUntil);
    }

    [Fact]
    public void A_possible_match_is_referred_with_a_case_due_in_48_hours_and_the_matches_kept()
    {
        var matches = new[] { new WorldCheckMatchResult("SANCTIONS", 0.82, "List entry (test data)") };

        var screening = ScreeningRecord.From(Guid.NewGuid(), Subject, new WorldCheckResult("wc_1", true, matches), Now);

        Assert.Equal(ScreeningStatus.Referred, screening.Status);
        Assert.NotNull(screening.Case);
        Assert.Equal(Now.AddHours(48), screening.Case.DueAt);
        Assert.True(screening.Case.IsOpen);
        Assert.Equal("SANCTIONS", Assert.Single(screening.Matches).ListType);
    }

    [Theory]
    [InlineData(Decisions.Clear, ScreeningStatus.Cleared)]
    [InlineData(Decisions.Reject, ScreeningStatus.Rejected)]
    public void The_officers_decision_sets_the_outcome_and_is_attributed(string decision, string status)
    {
        var screening = Referred();

        screening.Case!.Decide(decision, "Reason.", "officer-mb-1", Now.AddHours(3));

        Assert.Equal(status, screening.Status);
        Assert.False(screening.Case.IsOpen);
        Assert.Equal(("officer-mb-1", Now.AddHours(3)), (screening.Case.DecidedBy, screening.Case.DecidedAt));
    }

    [Fact]
    public void A_case_is_decided_once()
    {
        var screening = Referred();
        screening.Case!.Decide(Decisions.Clear, "Reason.", "officer-mb-1", Now);

        Assert.Throws<InvalidOperationException>(() => screening.Case.Decide(Decisions.Reject, "Reason.", "officer-mb-2", Now));
        Assert.Equal(ScreeningStatus.Cleared, screening.Status);
    }

    [Fact]
    public void An_overdue_case_stays_open()
    {
        var screening = Referred();

        // Nothing in the model takes a time and closes the case: only Decide changes it.
        Assert.True(screening.Case!.IsOpen);
        Assert.True(screening.Case.DueAt < Now.AddDays(3));
        Assert.Equal(ScreeningStatus.Referred, screening.Status);
    }

    private static ScreeningRecord Referred()
    {
        var screening = ScreeningRecord.From(Guid.NewGuid(), Subject, new WorldCheckResult("wc_1", true, [new WorldCheckMatchResult("PEP", 0.7, "Entry")]), Now);
        screening.Case!.Screening = screening;
        return screening;
    }
}
