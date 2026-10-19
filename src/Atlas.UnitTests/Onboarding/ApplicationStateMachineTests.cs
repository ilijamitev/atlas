using Atlas.Onboarding.Applications;
using Atlas.Shared.Contracts;
using Atlas.Shared.Markets;

namespace Atlas.UnitTests.Onboarding;

/// <summary>Every transition of the application, with no I/O.</summary>
public sealed class ApplicationStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly MarketOptions Remote = new() { Country = "XB", ConfidenceThreshold = 0.85, MaxRetakes = 2 };
    private static readonly MarketOptions InBranch = new() { Country = "XD", Activation = ActivationMode.InBranch, ConfidenceThreshold = 0.85, MaxRetakes = 2 };
    private static readonly ReviewSchedule Schedule = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
    private static readonly IdentificationResult Valid = new("idn_1", "VALID", true, 0.97);

    [Fact]
    public void Submitting_a_draft_starts_retention_and_queues_identification()
    {
        var application = Draft();

        application.Submit(Now);

        Assert.Equal(ApplicationState.Verifying, application.State);
        Assert.Equal(Now, application.SubmittedAt);
        Assert.Equal(Now + Application.Retention, application.RetentionUntil);
        Assert.Equal(Now, application.NextAttemptAt);
    }

    [Fact]
    public void A_valid_identification_goes_on_to_screening()
    {
        var application = Verifying();

        application.RecordIdentification(Valid, Remote, Now);

        Assert.Equal(ApplicationState.Screening, application.State);
        Assert.Equal(Now, application.NextAttemptAt);
    }

    [Fact]
    public void An_invalid_document_is_rejected_and_never_screened()
    {
        var application = Verifying();

        application.RecordIdentification(Valid with { DocumentResult = "INVALID" }, Remote, Now);

        Assert.Equal(ApplicationState.Rejected, application.State);
        Assert.Equal(RejectionReasons.DocumentInvalid, application.RejectionReason);
        Assert.Equal(Now, application.DecidedAt);
        Assert.Null(application.NextAttemptAt);
    }

    [Theory]
    [InlineData("INCONCLUSIVE", true, 0.55, "identity.inconclusive")]
    [InlineData("VALID", false, 0.41, "identity.faceMismatch")]
    [InlineData("VALID", true, 0.62, "identity.lowConfidence")]
    public void An_unclear_result_asks_for_a_retake_and_says_why(string documentResult, bool faceMatch, double confidence, string reason)
    {
        var application = Verifying();

        application.RecordIdentification(new IdentificationResult("idn_1", documentResult, faceMatch, confidence), Remote, Now);

        Assert.Equal(ApplicationState.ActionRequired, application.State);
        Assert.Equal(reason, application.History[^1].Reason);
        Assert.Null(application.NextAttemptAt);
    }

    [Fact]
    public void When_the_retakes_run_out_the_applicant_is_rejected()
    {
        var application = Verifying();
        var unclear = Valid with { FaceMatch = false };

        for (var retake = 0; retake < Remote.MaxRetakes; retake++)
        {
            application.RecordIdentification(unclear, Remote, Now);
            Assert.Equal(ApplicationState.ActionRequired, application.State);
            application.Submit(Now);
        }

        application.RecordIdentification(unclear, Remote, Now);

        Assert.Equal(ApplicationState.Rejected, application.State);
        Assert.Equal(RejectionReasons.IdentityNotVerified, application.RejectionReason);
        Assert.Equal(Remote.MaxRetakes, application.RetakeCount);
    }

    [Fact]
    public void Cleared_in_a_remote_market_opens_the_account_at_once()
    {
        var application = Screening();

        application.RecordScreening(ScreeningStatus.Cleared, Remote, Schedule, Now);

        Assert.Equal(ApplicationState.OpeningAccount, application.State);
        Assert.Equal(Now, application.NextAttemptAt);
    }

    [Fact]
    public void Cleared_in_a_branch_market_waits_for_the_branch_and_nothing_else()
    {
        var application = Screening();

        application.RecordScreening(ScreeningStatus.Cleared, InBranch, Schedule, Now);

        Assert.Equal(ApplicationState.AwaitingBranchVisit, application.State);
        Assert.Null(application.NextAttemptAt);
    }

    [Fact]
    public void A_referral_is_checked_after_a_minute_then_less_often_up_to_every_15_minutes()
    {
        var application = Screening();
        var checks = new List<TimeSpan>();

        application.RecordScreening(ScreeningStatus.Referred, Remote, Schedule, Now);
        checks.Add(application.NextAttemptAt!.Value - Now);
        for (var i = 0; i < 5; i++)
        {
            application.RecordScreening(ScreeningStatus.Referred, Remote, Schedule, Now);
            checks.Add(application.NextAttemptAt!.Value - Now);
        }

        Assert.Equal(ApplicationState.UnderReview, application.State);
        Assert.Equal([1, 2, 4, 8, 15, 15], checks.Select(c => c.TotalMinutes));
    }

    [Fact]
    public void Only_the_officers_answer_moves_a_referred_application_on()
    {
        var cleared = UnderReview();
        var rejected = UnderReview();

        cleared.RecordScreening(ScreeningStatus.Cleared, Remote, Schedule, Now);
        rejected.RecordScreening(ScreeningStatus.Rejected, Remote, Schedule, Now);

        Assert.Equal(ApplicationState.OpeningAccount, cleared.State);
        Assert.Equal(ApplicationState.Rejected, rejected.State);
        Assert.Equal(RejectionReasons.ScreeningRejected, rejected.RejectionReason);
    }

    [Fact]
    public void Nothing_else_can_open_an_account_for_an_application_under_review()
    {
        var application = UnderReview();

        Assert.Throws<InvalidTransitionException>(() => application.RecordAccount("acc_1"));
        Assert.Throws<InvalidTransitionException>(() => application.RecordCard("card_1", Now));
        Assert.Throws<InvalidTransitionException>(() => application.ActivateInBranch("branch-md-1", Now));
        Assert.Throws<InvalidTransitionException>(() => application.Submit(Now));
    }

    [Fact]
    public void Approved_means_the_account_is_open_and_the_card_ordered()
    {
        var application = OpeningAccount(Remote);

        Assert.Throws<InvalidOperationException>(() => application.RecordCard("card_1", Now));
        application.RecordAccount("acc_1");
        application.RecordCard("card_1", Now);

        Assert.Equal(ApplicationState.Approved, application.State);
        Assert.Equal(("acc_1", "card_1"), (application.AccountReference, application.CardReference));
        Assert.Equal(Now, application.DecidedAt);
        Assert.Null(application.NextAttemptAt);
    }

    [Fact]
    public void Branch_staff_activation_opens_the_account()
    {
        var application = Screening();
        application.RecordScreening(ScreeningStatus.Cleared, InBranch, Schedule, Now);

        application.ActivateInBranch("branch-md-1", Now);

        Assert.Equal(ApplicationState.OpeningAccount, application.State);
        Assert.Equal(("branch-md-1", Now), (application.BranchActivatedBy, application.BranchActivatedAt));
        Assert.Equal(Now, application.NextAttemptAt);
    }

    [Fact]
    public void A_technical_failure_keeps_the_state_and_schedules_a_retry()
    {
        var application = Verifying();

        application.Park("idnow: HTTP 503", TimeSpan.FromSeconds(10), Now);

        Assert.Equal(ApplicationState.Verifying, application.State);
        Assert.Equal((1, "idnow: HTTP 503"), (application.RetryCount, application.LastError));
        Assert.Equal(Now.AddSeconds(10), application.NextAttemptAt);

        application.RecordIdentification(Valid, Remote, Now);
        Assert.Equal((0, (string?)null), (application.RetryCount, application.LastError));
    }

    [Fact]
    public void A_permanent_failure_stops_the_retries_without_changing_the_state()
    {
        var application = OpeningAccount(Remote);

        application.FlagForOperations("corebanking: HTTP 422", Now);

        Assert.Equal(ApplicationState.OpeningAccount, application.State);
        Assert.True(application.NeedsAttention);
        Assert.Null(application.NextAttemptAt);
    }

    [Fact]
    public void A_restricted_application_stops_moving()
    {
        var application = Verifying();

        application.Restrict(Now);

        Assert.True(application.IsRestricted);
        Assert.Null(application.NextAttemptAt);
        Assert.Throws<InvalidTransitionException>(() => application.RecordIdentification(Valid, Remote, Now));
    }

    [Fact]
    public void A_draft_is_deleted_on_erasure_never_restricted()
    {
        Assert.Throws<InvalidOperationException>(() => Draft().Restrict(Now));
    }

    [Fact]
    public void Every_state_change_is_recorded()
    {
        var application = OpeningAccount(Remote);
        application.RecordAccount("acc_1");
        application.RecordCard("card_1", Now);

        Assert.Equal(
            [ApplicationState.Draft, ApplicationState.Verifying, ApplicationState.Screening, ApplicationState.OpeningAccount, ApplicationState.Approved],
            application.History.Select(h => h.To));
    }

    private static Application Draft() => Application.Start(new Applicant(), "key", [], [], Now);

    private static Application Verifying()
    {
        var application = Draft();
        application.Submit(Now);
        return application;
    }

    private static Application Screening()
    {
        var application = Verifying();
        application.RecordIdentification(Valid, Remote, Now);
        return application;
    }

    private static Application UnderReview()
    {
        var application = Screening();
        application.RecordScreening(ScreeningStatus.Referred, Remote, Schedule, Now);
        return application;
    }

    private static Application OpeningAccount(MarketOptions market)
    {
        var application = Screening();
        application.RecordScreening(ScreeningStatus.Cleared, market, Schedule, Now);
        return application;
    }
}
