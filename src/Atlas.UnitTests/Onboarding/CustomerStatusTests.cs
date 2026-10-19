using Atlas.Onboarding.Applications;
using Atlas.Shared.Contracts;
using Atlas.Shared.Markets;

namespace Atlas.UnitTests.Onboarding;

/// <summary>What the app is told: a neutral status, the next action and a message code, for every state.</summary>
public sealed class CustomerStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly MarketOptions Remote = new() { Country = "XB", MaxRetakes = 2 };
    private static readonly ReviewSchedule Schedule = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));

    [Fact]
    public void An_incomplete_draft_asks_for_details_and_lists_them()
    {
        var status = ApplicationViews.Status(Draft());

        Assert.Equal((CustomerStatus.Draft, NextAction.CompleteDetails, "application.incomplete"), (status.Status, status.NextAction, status.MessageCode));
        Assert.Contains("documents.selfie", status.MissingFields);
    }

    [Fact]
    public void The_worker_states_all_read_as_in_progress()
    {
        var verifying = Verifying();
        var screening = Verifying();
        screening.RecordIdentification(new IdentificationResult("idn", "VALID", true, 0.97), Remote, Now);

        Assert.All(new[] { verifying, screening }, application =>
        {
            var status = ApplicationViews.Status(application);
            Assert.Equal((CustomerStatus.InProgress, NextAction.Wait), (status.Status, status.NextAction));
            Assert.Empty(status.MissingFields);
        });
    }

    [Fact]
    public void A_referral_reads_as_a_neutral_under_review()
    {
        var application = Verifying();
        application.RecordIdentification(new IdentificationResult("idn", "VALID", true, 0.97), Remote, Now);
        application.RecordScreening(ScreeningStatus.Referred, Remote, Schedule, Now);

        var status = ApplicationViews.Status(application);

        Assert.Equal((CustomerStatus.UnderReview, NextAction.Wait, "application.underReview"), (status.Status, status.NextAction, status.MessageCode));
        Assert.DoesNotContain("screen", status.MessageCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("match", status.MessageCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_rejection_reads_the_same_whatever_the_reason()
    {
        var invalidDocument = Verifying();
        invalidDocument.RecordIdentification(new IdentificationResult("idn", "INVALID", false, 0.3), Remote, Now);
        var officerRejected = Verifying();
        officerRejected.RecordIdentification(new IdentificationResult("idn", "VALID", true, 0.97), Remote, Now);
        officerRejected.RecordScreening(ScreeningStatus.Rejected, Remote, Schedule, Now);

        var document = ApplicationViews.Status(invalidDocument);
        var officer = ApplicationViews.Status(officerRejected);
        Assert.Equal((document.Status, document.NextAction, document.MessageCode), (officer.Status, officer.NextAction, officer.MessageCode));
    }

    [Fact]
    public void A_retake_asks_for_a_photo()
    {
        var application = Verifying();
        application.RecordIdentification(new IdentificationResult("idn", "VALID", false, 0.4), Remote, Now);

        Assert.Equal(NextAction.RetakePhoto, ApplicationViews.Status(application).NextAction);
    }

    [Fact]
    public void A_restricted_application_reads_as_withdrawn_whatever_its_state()
    {
        var application = Verifying();
        application.Restrict(Now);

        var status = ApplicationViews.Status(application);

        Assert.Equal((CustomerStatus.Withdrawn, NextAction.None), (status.Status, status.NextAction));
    }

    [Theory]
    [InlineData("1234567890123", "*********0123")]
    [InlineData("AB12", "****")]
    [InlineData("A", "*")]
    public void The_applicant_sees_only_the_last_four_characters(string value, string masked)
    {
        Assert.Equal(masked, ApplicationViews.Mask(value));
    }

    private static Application Draft() => Application.Start(new Applicant(), "key", [], [], Now);

    private static Application Verifying()
    {
        var application = Draft();
        application.Submit(Now);
        return application;
    }
}
