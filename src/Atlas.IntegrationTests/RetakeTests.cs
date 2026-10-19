using System.Net;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;

namespace Atlas.IntegrationTests;

/// <summary>IDNow's answer decides what happens next: a retake while the market's retakes last, a rejection after.</summary>
[Collection(AtlasCollection.Name)]
public sealed class RetakeTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Low_confidence_asks_for_a_retake_and_a_new_photo_is_approved()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", selfieMarker: "LOW_CONFIDENCE"));
        Assert.Equal((CustomerStatus.ActionRequired, NextAction.RetakePhoto), (created.Status, created.NextAction));

        var tooEarly = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);
        Assert.Equal(["documents.retakeNeeded"], await tooEarly.FieldCodesAsync());

        var details = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { email = "other@example.test" });
        Assert.Equal("application.onlyPhotos", await details.ProblemCodeAsync());

        var photo = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { documents = new[] { new { type = "SELFIE", image = TestData.Image() } } });
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);

        var submitted = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);
        Assert.Equal(CustomerStatus.Approved, (await submitted.ReadAsync<ApplicationStatusResponse>()).Status);
        Assert.Equal(
            [$"{created.ApplicationId}:idnow:0", $"{created.ApplicationId}:idnow:1"],
            (await atlas.JournalAsync("idnow", created.ApplicationId)).Select(call => call.IdempotencyKey));
    }

    [Fact]
    public async Task When_the_retakes_run_out_the_application_is_rejected()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", selfieMarker: "FACE_MISMATCH"));

        for (var retake = 1; retake <= 2; retake++)
        {
            await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { documents = new[] { new { type = "SELFIE", image = TestData.Image("FACE_MISMATCH") } } });
            var submitted = await app.SubmitAsync("MB", created.ApplicationId, created.ApplicationToken);
            var expected = retake < 2 ? CustomerStatus.ActionRequired : CustomerStatus.Rejected;
            Assert.Equal(expected, (await submitted.ReadAsync<ApplicationStatusResponse>()).Status);
        }

        Assert.Empty(await atlas.JournalAsync("worldcheck", created.ApplicationId));
    }

    [Fact]
    public async Task An_invalid_document_is_rejected_before_it_reaches_screening()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());

        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", documentMarker: "INVALID"));

        Assert.Equal(CustomerStatus.Rejected, created.Status);
        Assert.Empty(await atlas.JournalAsync("worldcheck", created.ApplicationId));
    }
}
