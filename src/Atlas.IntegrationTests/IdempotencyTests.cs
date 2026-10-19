using System.Net;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

/// <summary>A dropped connection and a retry must never create a second application or a second identification.</summary>
[Collection(AtlasCollection.Name)]
public sealed class IdempotencyTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_same_key_and_body_return_the_same_application_with_a_new_token()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var key = Guid.NewGuid().ToString();
        var body = TestData.Complete("MB");

        var first = await app.CreateOkAsync("MB", body, key);
        var second = await app.CreateOkAsync("MB", body, key);

        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.Equal(CustomerStatus.Approved, second.Status);
        Assert.NotEqual(first.ApplicationToken, second.ApplicationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MB", first.ApplicationId, first.ApplicationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("MB", first.ApplicationId, second.ApplicationToken)).StatusCode);
        Assert.Single(await atlas.JournalAsync("idnow", first.ApplicationId));
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var key = Guid.NewGuid().ToString();
        await app.CreateOkAsync("MB", TestData.Complete("MB"), key);

        var response = await app.CreateAsync("MB", TestData.Complete("MB", lastName: "Other"), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("idempotencyKey.reused", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Concurrent_creates_with_one_key_make_one_application()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var key = Guid.NewGuid().ToString();
        var body = TestData.Complete("MB");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => app.CreateAsync("MB", body, key)));

        // Racing requests either get the application or a 409 that says to try again; nothing else.
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var final = await app.CreateOkAsync("MB", body, key);
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            Assert.Equal(final.ApplicationId, (await response.ReadAsync<ApplicationCreatedResponse>()).ApplicationId);
        }

        await app.WaitForStatusAsync("MB", final.ApplicationId, final.ApplicationToken, CustomerStatus.Approved);
        await using var db = atlas.Onboarding.Services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>().Create("MB");
        Assert.Equal(1, await db.Applications.CountAsync(a => a.IdempotencyKey == key));
        Assert.Single(await atlas.JournalAsync("idnow", final.ApplicationId), call => call.Status == 200);
    }

    [Fact]
    public async Task Submitting_twice_answers_twice_and_identifies_once()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Without(TestData.Complete("MB"), "termsAccepted"));
        await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { termsAccepted = true });

        var first = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);
        var second = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(CustomerStatus.Approved, (await second.ReadAsync<ApplicationStatusResponse>()).Status);
        Assert.Single(await atlas.JournalAsync("idnow", created.ApplicationId));
    }

    [Fact]
    public async Task Create_without_an_Idempotency_Key_is_refused()
    {
        var client = atlas.Onboarding.CreateClient();

        var response = await client.PostAsync("/v1/markets/MB/applications", System.Net.Http.Json.JsonContent.Create(TestData.Complete("MB")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["idempotencyKey.required"], await response.FieldCodesAsync());
    }
}
