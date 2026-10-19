using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

/// <summary>"If a provider is down ... build in retries": an outage parks the application, and nobody is rejected for it.</summary>
[Collection(AtlasCollection.Name)]
public sealed class OutageTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => atlas.ResetAsync();

    [Fact]
    public async Task IDNow_down_parks_the_application_and_it_resumes_with_one_successful_identification()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        await atlas.SetFaultsAsync("idnow", "down");

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        Assert.Equal(CustomerStatus.InProgress, created.Status);
        Assert.Equal(NextAction.Wait, created.NextAction);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(CustomerStatus.InProgress, (await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken)).Status);

        await atlas.SetFaultsAsync("idnow", "healthy");
        await app.WaitForStatusAsync("MB", created.ApplicationId, created.ApplicationToken, CustomerStatus.Approved);

        var identifications = await atlas.JournalAsync("idnow", created.ApplicationId);
        Assert.Contains(identifications, call => call.Status == 503);
        Assert.Single(identifications, call => call.Status == 200);
        Assert.All(identifications, call => Assert.Equal($"{created.ApplicationId}:idnow:0", call.IdempotencyKey));
    }

    [Fact]
    public async Task World_Check_down_parks_the_application_in_screening_until_it_is_back()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        await atlas.SetFaultsAsync("worldcheck", "error500");

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));
        Assert.Equal(CustomerStatus.InProgress, created.Status);

        await atlas.SetFaultsAsync("worldcheck", "healthy");
        await app.WaitForStatusAsync("MB", created.ApplicationId, created.ApplicationToken, CustomerStatus.Approved);

        var screens = await atlas.JournalAsync("worldcheck", created.ApplicationId);
        Assert.Contains(screens, call => call.Status == 500);
        Assert.Single(screens, call => call.Status == 200);
    }

    [Fact]
    public async Task A_few_failed_calls_are_absorbed_by_the_in_call_retries()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        await atlas.SetFaultsAsync("corebanking", "healthy", failNext: 2);

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        Assert.Equal(CustomerStatus.Approved, created.Status);
        var calls = await atlas.JournalAsync("corebanking", created.ApplicationId);
        Assert.Equal([503, 503, 201, 201], calls.Select(call => call.Status));
    }

    [Fact]
    public async Task A_provider_that_never_answers_times_out_and_the_application_resumes()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        await atlas.SetFaultsAsync("idnow", "timeout");

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));
        Assert.Equal(CustomerStatus.InProgress, created.Status);

        await atlas.SetFaultsAsync("idnow", "healthy");
        await app.WaitForStatusAsync("MB", created.ApplicationId, created.ApplicationToken, CustomerStatus.Approved, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_permanent_refusal_stops_the_retries_and_flags_the_application_without_rejecting_it()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        await atlas.SetFaultsAsync("corebanking", "reject");

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(CustomerStatus.InProgress, created.Status);
        Assert.Equal(CustomerStatus.InProgress, (await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken)).Status);
        Assert.Single(await atlas.JournalAsync("corebanking", created.ApplicationId));

        await using var db = atlas.Onboarding.Services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>().Create("MB");
        var application = await db.Applications.SingleAsync(a => a.Id == created.ApplicationId);
        Assert.True(application.NeedsAttention);
        Assert.Equal(ApplicationState.OpeningAccount, application.State);
        Assert.Null(application.NextAttemptAt);
        Assert.Equal("corebanking: HTTP 422", application.LastError);
    }
}
