using System.Net;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;

namespace Atlas.IntegrationTests;

/// <summary>Drafts live on the server: create with what the app has, PATCH the rest, submit.</summary>
[Collection(AtlasCollection.Name)]
public sealed class SaveAndResumeTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_partial_create_is_a_draft_that_says_what_is_missing_and_can_be_finished_later()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");

        var created = await app.CreateOkAsync("MB", TestData.Only(body, "firstName", "lastName"));
        Assert.Equal(CustomerStatus.Draft, created.Status);
        Assert.Equal(NextAction.CompleteDetails, created.NextAction);
        Assert.Contains("dateOfBirth", created.MissingFields);
        Assert.Contains("documents.selfie", created.MissingFields);

        var patched = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, TestData.Without(body, "firstName", "lastName", "termsAccepted"));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal(["termsAccepted"], (await patched.ReadAsync<ApplicationStatusResponse>()).MissingFields);

        var accepted = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { termsAccepted = true });
        var ready = await accepted.ReadAsync<ApplicationStatusResponse>();
        Assert.Equal((CustomerStatus.Draft, NextAction.Submit), (ready.Status, ready.NextAction));

        var submitted = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.Equal(CustomerStatus.Approved, (await submitted.ReadAsync<ApplicationStatusResponse>()).Status);
    }

    [Fact]
    public async Task Submitting_an_incomplete_draft_lists_every_missing_field()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", new { firstName = TestData.FirstName });

        var response = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = await response.FieldCodesAsync();
        Assert.Contains("lastName.required", codes);
        Assert.Contains("identifier.required", codes);
        Assert.Contains("documents.identity.required", codes);
        Assert.DoesNotContain("firstName.required", codes);
    }

    [Fact]
    public async Task A_submitted_application_cannot_be_changed()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        var response = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { email = "other@example.test" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application.notEditable", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_new_photo_on_a_draft_is_a_new_version_and_the_old_one_is_kept()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", new { documents = new[] { new { type = "PASSPORT", image = TestData.Image() } } });

        await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { documents = new[] { new { type = "PASSPORT", image = TestData.Image() } } });

        var details = await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken);
        Assert.Equal([1, 2], details.Documents.Where(d => d.Type == "PASSPORT").Select(d => d.Version).Order());
    }
}
