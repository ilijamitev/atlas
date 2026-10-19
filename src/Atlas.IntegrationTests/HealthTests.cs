using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Atlas.IntegrationTests;

[Collection(AtlasCollection.Name)]
public sealed class HealthTests(AtlasFixture atlas)
{
    [Fact]
    public async Task Onboarding_and_Screening_report_every_market_database()
    {
        foreach (var (client, service) in new[] { (atlas.Onboarding.CreateClient(), "onboarding"), (atlas.Screening.CreateClient(), "screening") })
        {
            var report = await ReadyAsync(client);

            var databases = Details(report, $"sql-{service}");
            Assert.Equal(3, databases.Count);
            Assert.All(databases, name => Assert.Contains($"_{service}_", name));
        }
    }

    [Fact]
    public async Task Onboarding_reports_every_market_document_container()
    {
        var report = await ReadyAsync(atlas.Onboarding.CreateClient());

        var containers = Details(report, "blob-documents");
        Assert.Equal(["-ma", "-mb", "-md"], containers.Select(name => name[^3..]).Order());
    }

    [Fact]
    public async Task Every_service_is_live()
    {
        foreach (var client in new[] { atlas.Onboarding.CreateClient(), atlas.Screening.CreateClient(), atlas.Simulators.CreateClient() })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
    }

    private static async Task<JsonElement> ReadyAsync(HttpClient client)
    {
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", report.GetProperty("status").GetString());
        return report;
    }

    private static List<string> Details(JsonElement report, string check) =>
        report.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == check)
            .GetProperty("details").EnumerateObject().Select(d => d.Name).ToList();
}
