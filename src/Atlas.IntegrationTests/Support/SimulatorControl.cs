using System.Net;
using System.Net.Http.Json;
using Atlas.Simulators.Control;

namespace Atlas.IntegrationTests.Support;

/// <summary>Drives the stand-ins over HTTP, exactly as a reviewer would.</summary>
internal static class SimulatorControl
{
    public static async Task ResetAsync(this AtlasFixture atlas)
    {
        var response = await atlas.Simulators.CreateClient().DeleteAsync("/_control");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public static async Task SetFaultsAsync(this AtlasFixture atlas, string provider, string mode, int failNext = 0, int? latencyMs = null)
    {
        var response = await atlas.Simulators.CreateClient().PutAsJsonAsync($"/_control/{provider}", new { mode, failNext, latencyMs });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static async Task<List<JournalEntry>> JournalAsync(this AtlasFixture atlas, string? provider = null, Guid? applicationId = null)
    {
        var query = new List<string>();
        if (provider is not null)
        {
            query.Add($"provider={provider}");
        }

        if (applicationId is not null)
        {
            query.Add($"keyPrefix={applicationId}");
        }

        var path = "/_control/requests" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        return (await atlas.Simulators.CreateClient().GetFromJsonAsync<List<JournalEntry>>(path))!;
    }
}
