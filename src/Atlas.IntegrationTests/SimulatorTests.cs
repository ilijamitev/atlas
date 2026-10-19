using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.IntegrationTests.Support;
using Atlas.Simulators;
using Atlas.Simulators.Scenarios;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Atlas.IntegrationTests;

/// <summary>The stand-ins reviewers drive without editing code.</summary>
[Collection(AtlasCollection.Name)]
public sealed class SimulatorTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => atlas.ResetAsync();

    [Theory]
    [InlineData(null, "VALID", true)]
    [InlineData("INVALID", "INVALID", false)]
    [InlineData("INCONCLUSIVE", "INCONCLUSIVE", true)]
    [InlineData("FACE_MISMATCH", "VALID", false)]
    public async Task IDNow_answers_by_the_marker_in_the_image(string? marker, string documentResult, bool faceMatch)
    {
        var response = await atlas.Simulators.CreateClient().PostAsJsonAsync(
            "/idnow/v1/identifications",
            new { documentType = "PASSPORT", documentImage = TestData.Image(marker), selfieImage = TestData.Image() });

        var answer = await response.ReadAsync<IdNowResponse>();
        Assert.Equal((documentResult, faceMatch), (answer.DocumentResult, answer.FaceMatch));
    }

    [Fact]
    public async Task World_Check_matches_on_the_name()
    {
        var client = atlas.Simulators.CreateClient();

        var clear = await (await client.PostAsJsonAsync("/refinitiv/worldcheck/v2/screen", new { name = "Test Tester", dateOfBirth = TestData.DateOfBirth, nationality = "XB" })).ReadAsync<WorldCheckResponse>();
        var match = await (await client.PostAsJsonAsync("/refinitiv/worldcheck/v2/screen", new { name = "Test MATCH", dateOfBirth = TestData.DateOfBirth, nationality = "XB" })).ReadAsync<WorldCheckResponse>();

        Assert.Equal(("CLEAR", 0), (clear.Status, clear.Matches.Count));
        Assert.Equal("POSSIBLE_MATCH", match.Status);
        Assert.Equal("SANCTIONS", Assert.Single(match.Matches).ListType);
    }

    [Fact]
    public async Task FailNext_fails_that_many_calls_then_the_mode_applies_again()
    {
        var client = atlas.Simulators.CreateClient();
        await atlas.SetFaultsAsync("worldcheck", "healthy", failNext: 2);
        var request = new { name = "Test Tester", dateOfBirth = TestData.DateOfBirth, nationality = "XB" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/refinitiv/worldcheck/v2/screen", request)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK], statuses);
    }

    [Fact]
    public async Task Fault_control_refuses_unknown_providers_and_modes()
    {
        var client = atlas.Simulators.CreateClient();

        var provider = await client.PutAsJsonAsync("/_control/payments", new { mode = "down" });
        var mode = await client.PutAsJsonAsync("/_control/idnow", new { mode = "sideways" });

        Assert.Equal(HttpStatusCode.NotFound, provider.StatusCode);
        Assert.Equal(["mode.unknown"], await mode.FieldCodesAsync());
    }

    [Fact]
    public async Task Core_banking_opens_one_account_per_application_however_often_it_is_asked()
    {
        var client = atlas.Simulators.CreateClient();
        var request = new { applicationId = Guid.NewGuid(), market = "MB", holder = new { firstName = TestData.FirstName, lastName = "Tester", dateOfBirth = TestData.DateOfBirth } };

        var first = await client.PostAsJsonAsync("/corebanking/v1/accounts", request);
        var second = await client.PostAsJsonAsync("/corebanking/v1/accounts", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await first.ReadAsync<AccountResponse>()).AccountId, (await second.ReadAsync<AccountResponse>()).AccountId);
    }

    [Fact]
    public async Task The_journal_keeps_keys_and_outcomes_but_never_the_request_body()
    {
        var client = atlas.Simulators.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/refinitiv/worldcheck/v2/screen")
        {
            Content = JsonContent.Create(new { name = "Test MATCH", dateOfBirth = TestData.DateOfBirth, nationality = "XB" }),
        };
        request.Headers.Add("Idempotency-Key", "journal-test:worldcheck");
        await client.SendAsync(request);

        var raw = await client.GetStringAsync("/_control/requests?provider=worldcheck");

        Assert.Contains("journal-test:worldcheck", raw);
        Assert.Contains("POSSIBLE_MATCH (SANCTIONS)", raw);
        Assert.DoesNotContain("MATCH\"", raw);
        Assert.DoesNotContain(TestData.DateOfBirth, raw);
    }

    [Fact]
    public async Task Dev_tokens_name_the_person_role_and_market()
    {
        var client = atlas.Simulators.CreateClient();

        var response = await client.PostAsJsonAsync("/dev/token", new { role = "officer", market = "mb", subject = "officer-mb-3" });
        var refused = await client.PostAsJsonAsync("/dev/token", new { role = "officer", market = "MC" });

        var token = await response.ReadAsync<DevTokenResponse>();
        var claims = new JsonWebToken(token.AccessToken).Claims.ToDictionary(c => c.Type, c => c.Value);
        Assert.Equal(("officer-mb-3", "officer", "MB"), (claims["sub"], claims["role"], claims["market"]));
        Assert.Equal(["market.unknown"], await refused.FieldCodesAsync());
    }

    [Fact]
    public async Task Every_provider_starts_healthy_after_a_reset()
    {
        var client = atlas.Simulators.CreateClient();
        await atlas.SetFaultsAsync("idnow", "down", latencyMs: 5);

        await atlas.ResetAsync();

        var faults = await client.GetFromJsonAsync<List<ProviderFaults>>("/_control/", JsonSerializerOptions.Web);
        Assert.All(faults!, f => Assert.Equal(("healthy", 0), (f.Mode, f.FailNext)));
    }
}
