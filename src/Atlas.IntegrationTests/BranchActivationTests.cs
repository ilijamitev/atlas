using System.Net;
using System.Net.Http.Json;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;
using Atlas.Shared.Auth;

namespace Atlas.IntegrationTests;

/// <summary>Annex B, MD: remote identification for the application stage only; the account waits for a branch visit.</summary>
[Collection(AtlasCollection.Name)]
public sealed class BranchActivationTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MD_waits_for_a_branch_visit_and_branch_staff_activate_it()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MD", TestData.Complete("MD"));

        Assert.Equal(CustomerStatus.AwaitingBranchVisit, created.Status);
        Assert.Equal(NextAction.VisitBranch, created.NextAction);
        Assert.Empty(await atlas.JournalAsync("corebanking", created.ApplicationId));

        var staff = StaffClient("MD");
        var view = await staff.GetFromJsonAsync<ApplicationDetailsResponse>($"/v1/markets/MD/branch/applications/{created.ApplicationId}");
        Assert.Equal(TestData.Markets["MD"].Identifier, view!.Applicant.Identifier!.Value);

        var activation = await staff.PostAsJsonAsync(
            $"/v1/markets/MD/branch/applications/{created.ApplicationId}/activation",
            new { identityDocumentChecked = true, wetSignatureCollected = true });

        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(CustomerStatus.Approved, (await activation.ReadAsync<ApplicationStatusResponse>()).Status);
        Assert.Equal(["account", "card"], (await atlas.JournalAsync("corebanking", created.ApplicationId)).Select(e => e.Operation));
    }

    [Fact]
    public async Task Activation_needs_both_confirmations()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MD", TestData.Complete("MD"));

        var response = await StaffClient("MD").PostAsJsonAsync(
            $"/v1/markets/MD/branch/applications/{created.ApplicationId}/activation",
            new { identityDocumentChecked = true, wetSignatureCollected = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["branch.confirmationsRequired"], await response.FieldCodesAsync());
    }

    [Fact]
    public async Task Only_branch_staff_of_the_market_can_activate()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MD", TestData.Complete("MD"));
        var path = $"/v1/markets/MD/branch/applications/{created.ApplicationId}/activation";
        var body = new { identityDocumentChecked = true, wetSignatureCollected = true };

        var anonymous = await atlas.Onboarding.CreateClient().PostAsJsonAsync(path, body);
        var otherMarket = await StaffClient("MB").PostAsJsonAsync(path, body);
        var applicant = await atlas.Onboarding.CreateClient().SendAsync(WithBody(OnboardingClient.Request(HttpMethod.Post, path, created.ApplicationToken), body));
        var officer = await atlas.Onboarding.CreateClient().SendAsync(WithBody(OnboardingClient.Request(HttpMethod.Post, path, bearer: atlas.Token(Roles.Officer, "MD")), body));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherMarket.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, applicant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, officer.StatusCode);
    }

    [Fact]
    public async Task A_remote_market_never_waits_for_a_branch()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        var response = await StaffClient("MB").PostAsJsonAsync(
            $"/v1/markets/MB/branch/applications/{created.ApplicationId}/activation",
            new { identityDocumentChecked = true, wetSignatureCollected = true });

        Assert.Equal(CustomerStatus.Approved, created.Status);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application.notAwaitingBranchVisit", await response.ProblemCodeAsync());
    }

    private HttpClient StaffClient(string market)
    {
        var client = atlas.Onboarding.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", atlas.Token(Roles.BranchStaff, market));
        return client;
    }

    private static HttpRequestMessage WithBody(HttpRequestMessage request, object body)
    {
        request.Content = JsonContent.Create(body);
        return request;
    }
}
