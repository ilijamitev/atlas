using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;

namespace Atlas.IntegrationTests;

[Collection(AtlasCollection.Name)]
public sealed class HappyPathTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_tickets_single_POST_is_approved_in_the_same_call()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        Assert.Equal(CustomerStatus.Approved, created.Status);
        Assert.Equal(NextAction.None, created.NextAction);
        Assert.Equal("application.approved", created.MessageCode);
    }

    [Fact]
    public async Task Each_provider_is_called_once_in_order_and_the_account_comes_after_screening()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        var journal = await atlas.JournalAsync(applicationId: created.ApplicationId);
        Assert.Equal(["identification", "screen", "account", "card"], journal.Select(e => e.Operation));
        Assert.All(journal, call => Assert.InRange(call.Status, 200, 201));
    }

    [Fact]
    public async Task The_applicant_can_read_the_application_back_with_the_identifier_masked()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MA", TestData.Complete("MA"));

        var details = await app.GetOkAsync("MA", created.ApplicationId, created.ApplicationToken);

        Assert.Equal(CustomerStatus.Approved, details.Status);
        Assert.Equal(TestData.FirstName, details.Applicant.FirstName);
        Assert.Equal("*********0123", details.Applicant.Identifier!.Value);
        Assert.Equal(["PASSPORT", "SELFIE"], details.Documents.Select(d => d.Type).Order());
        Assert.All(details.Documents, d => Assert.Equal("STORED", d.State));
        Assert.NotNull(details.SubmittedAt);
        Assert.NotNull(details.DecidedAt);
    }
}
