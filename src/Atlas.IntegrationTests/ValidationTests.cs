using System.Net;
using System.Text;
using Atlas.IntegrationTests.Support;

namespace Atlas.IntegrationTests;

/// <summary>Market rules come from config (Annex B), and every error carries a stable code and no echo of the input.</summary>
[Collection(AtlasCollection.Name)]
public sealed class ValidationTests(AtlasFixture atlas)
{
    [Fact]
    public async Task An_MB_identifier_in_the_wrong_format_is_a_field_error_that_does_not_echo_the_value()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["identifier"] = new { type = "NATIONAL_ID", value = "12AB56", issuingCountry = "XB" };

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ProblemCodeAsync());
        Assert.Equal(["identifier.format"], await response.FieldCodesAsync());
        Assert.DoesNotContain("12AB56", await response.BodyAsync());
    }

    [Fact]
    public async Task The_same_number_fits_MD_but_not_MB_because_the_rules_are_per_market()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["identifier"] = new { type = "NATIONAL_ID", value = TestData.Markets["MD"].Identifier, issuingCountry = "XB" };

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(["identifier.format"], await response.FieldCodesAsync());
    }

    [Fact]
    public async Task Only_MF_accepts_a_passport_number_instead_of_a_national_ID()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["identifier"] = new { type = "PASSPORT", value = "AB1234567", issuingCountry = "XX" };

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(["identifier.typeNotAccepted"], await response.FieldCodesAsync());
    }

    [Fact]
    public async Task Residents_of_other_countries_and_minors_are_refused_with_their_own_codes()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["countryOfResidence"] = "XA";
        body["dateOfBirth"] = DateTime.UtcNow.AddYears(-16).ToString("yyyy-MM-dd");

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(["countryOfResidence.notServed", "dateOfBirth.tooYoung"], (await response.FieldCodesAsync()).Order());
    }

    [Fact]
    public async Task Images_must_be_JPEG_or_PNG()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["documents"] = new[] { new { type = "PASSPORT", image = Convert.ToBase64String(Encoding.ASCII.GetBytes("not an image")) } };

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(["documents.image.format"], await response.FieldCodesAsync());
    }

    [Fact]
    public async Task An_oversized_body_is_refused_with_413()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var body = TestData.Complete("MB");
        body["documents"] = new[] { new { type = "PASSPORT", image = new string('A', AtlasFixture.MaxRequestBytes) } };

        var response = await app.CreateAsync("MB", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request.tooLarge", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_is_a_problem_not_a_crash()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/markets/MB/applications")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await atlas.Onboarding.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", await response.ProblemCodeAsync());
    }

    [Theory]
    [InlineData("MZ")]
    [InlineData("MC")]
    public async Task A_market_this_deployment_does_not_serve_is_404(string market)
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());

        var response = await app.CreateAsync(market, TestData.Complete("MB"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("market.unknown", await response.ProblemCodeAsync());
    }
}
