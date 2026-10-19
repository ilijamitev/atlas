using System.Net.Http.Json;
using System.Text.Json;

namespace Atlas.IntegrationTests;

/// <summary>The OpenAPI documents behind Swagger UI build, and say how to authenticate.</summary>
[Collection(AtlasCollection.Name)]
public sealed class OpenApiTests(AtlasFixture atlas)
{
    [Fact]
    public async Task Every_service_publishes_its_document_with_the_security_schemes()
    {
        foreach (var client in new[] { atlas.Onboarding.CreateClient(), atlas.Screening.CreateClient(), atlas.Simulators.CreateClient() })
        {
            var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");

            var schemes = document.GetProperty("components").GetProperty("securitySchemes");
            Assert.True(schemes.TryGetProperty("Bearer", out _));
            Assert.True(schemes.TryGetProperty("ApplicationToken", out _));
        }
    }

    [Fact]
    public async Task Applicant_endpoints_ask_for_the_application_token_and_officer_endpoints_for_a_bearer_token()
    {
        var onboarding = await atlas.Onboarding.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var screening = await atlas.Screening.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json");

        Assert.Equal("ApplicationToken", Scheme(onboarding, "/v1/markets/{market}/applications/{id}", "get"));
        Assert.Equal("Bearer", Scheme(screening, "/v1/markets/{market}/cases", "get"));
    }

    private static string Scheme(JsonElement document, string path, string method) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("security")[0].EnumerateObject().Single().Name;
}
