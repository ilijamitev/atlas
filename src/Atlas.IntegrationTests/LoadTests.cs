using System.Diagnostics;
using System.Net;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;

namespace Atlas.IntegrationTests;

/// <summary>Many applications at once: all finish, and each is identified exactly once, however the work is shared out.</summary>
[Collection(AtlasCollection.Name)]
[Trait("Category", "Load")]
public sealed class LoadTests(AtlasFixture atlas) : IAsyncLifetime
{
    private const int Applications = 50;

    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Fifty_concurrent_applications_all_finish_and_each_is_identified_once()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var clock = Stopwatch.StartNew();

        var responses = await Task.WhenAll(Enumerable.Range(0, Applications).Select(i =>
        {
            var market = i % 2 == 0 ? "MA" : "MB";
            return app.CreateAsync(market, TestData.Complete(market));
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var created = new List<(string Market, ApplicationCreatedResponse Body)>();
        for (var i = 0; i < responses.Length; i++)
        {
            created.Add((i % 2 == 0 ? "MA" : "MB", await responses[i].ReadAsync<ApplicationCreatedResponse>()));
        }

        foreach (var (market, body) in created.Where(c => c.Body.Status != CustomerStatus.Approved))
        {
            await app.WaitForStatusAsync(market, body.ApplicationId, body.ApplicationToken, CustomerStatus.Approved, TimeSpan.FromSeconds(60));
        }

        var identifications = await atlas.JournalAsync("idnow");
        foreach (var (_, body) in created)
        {
            Assert.Single(identifications, call => call.IdempotencyKey == $"{body.ApplicationId}:idnow:0");
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"Took {clock.Elapsed}.");
    }
}
