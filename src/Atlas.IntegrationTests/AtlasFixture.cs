using Atlas.Onboarding;
using Atlas.Onboarding.Clients;
using Atlas.Screening;
using Atlas.Screening.Screenings;
using Atlas.Shared.Auth;
using Atlas.Shared.Persistence;
using Atlas.Simulators;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atlas.IntegrationTests;

/// <summary>
/// All three services in one process, against the compose SQL Server and Azurite. Each run gets its own database and
/// container prefix and only the markets the tests use, and removes them when it's done. The services' HTTP clients
/// reach each other through the in-memory servers, with the resilience handlers still in the pipeline.
/// </summary>
public sealed class AtlasFixture : IAsyncLifetime
{
    public const string Markets = "MA,MB,MD";

    /// <summary>Lower than in production, so the oversized-body test doesn't have to send 24 MB.</summary>
    public const int MaxRequestBytes = 2 * 1024 * 1024;

    private readonly string _prefix = $"atlas_t{DateTime.UtcNow:yyyyMMddHHmmss}_{Random.Shared.Next(1000):D3}";

    public WebApplicationFactory<SimulatorsApi> Simulators { get; private set; } = null!;
    public WebApplicationFactory<ScreeningApi> Screening { get; private set; } = null!;
    public WebApplicationFactory<OnboardingApi> Onboarding { get; private set; } = null!;

    private string ContainerPrefix => _prefix.Replace('_', '-');

    public Task InitializeAsync()
    {
        Simulators = new WebApplicationFactory<SimulatorsApi>().WithWebHostBuilder(Configure);
        _ = Simulators.Server;

        Screening = new WebApplicationFactory<ScreeningApi>().WithWebHostBuilder(host =>
        {
            Configure(host);
            host.ConfigureTestServices(services => services.AddHttpClient(WorldCheckClient.Name).ConfigurePrimaryHttpMessageHandler(() => Simulators.Server.CreateHandler()));
        });
        _ = Screening.Server;

        Onboarding = new WebApplicationFactory<OnboardingApi>().WithWebHostBuilder(host =>
        {
            Configure(host);
            host.ConfigureTestServices(services =>
            {
                services.AddHttpClient(IdNowClient.Name).ConfigurePrimaryHttpMessageHandler(() => Simulators.Server.CreateHandler());
                services.AddHttpClient(CoreBankingClient.Name).ConfigurePrimaryHttpMessageHandler(() => Simulators.Server.CreateHandler());
                services.AddHttpClient(ScreeningClient.Name).ConfigurePrimaryHttpMessageHandler(() => Screening.Server.CreateHandler());
            });
        });

        // Start every host now, so migrations run once here rather than inside the first test.
        _ = Onboarding.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var sql = Onboarding.Services.GetRequiredService<IOptions<SqlOptions>>().Value;
        var blobs = Onboarding.Services.GetRequiredService<BlobServiceClient>();
        await foreach (var container in blobs.GetBlobContainersAsync(prefix: ContainerPrefix))
        {
            await blobs.DeleteBlobContainerAsync(container.Name);
        }

        await Onboarding.DisposeAsync();
        await Screening.DisposeAsync();
        await Simulators.DisposeAsync();
        await DropDatabasesAsync(sql);
    }

    /// <summary>A development JWT, as the simulator host's /dev/token would issue it.</summary>
    public string Token(string role, string? market, string? subject = null) =>
        Simulators.Services.GetRequiredService<DevTokenIssuer>().Issue(subject ?? $"{role}-{market?.ToLowerInvariant() ?? "atlas"}-test", role, market);

    private void Configure(IWebHostBuilder host) =>
        host.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atlas:Sql:DatabasePrefix"] = _prefix,
            ["Atlas:EnabledMarkets"] = Markets,
            ["Atlas:Blob:ContainerPrefix"] = ContainerPrefix,

            // Test runs stay out of the reviewers' Seq.
            ["Atlas:Seq:Url"] = "",

            // Fast everything: no simulator latency, short timeouts and backoffs, a busy worker, review checks every half second.
            ["Simulators:IdNowLatencyMs"] = "0",
            ["Simulators:WorldCheckLatencyMs"] = "0",
            ["Simulators:CoreBankingLatencyMs"] = "0",
            ["Atlas:Http:AttemptTimeout"] = "00:00:02",
            ["Atlas:Http:TotalTimeout"] = "00:00:05",
            ["Atlas:Http:RetryDelay"] = "00:00:00.050",
            ["Onboarding:CreatePermitLimit"] = "100000",
            ["Onboarding:MaxRequestBytes"] = MaxRequestBytes.ToString(),
            ["Onboarding:MaxImageBytes"] = "1048576",
            ["Onboarding:Worker:PollInterval"] = "00:00:00.100",
            ["Onboarding:Worker:RetryBaseDelay"] = "00:00:00.200",
            ["Onboarding:Worker:RetryMaxDelay"] = "00:00:01",
            ["Onboarding:Worker:ReviewFirstCheck"] = "00:00:00.200",
            ["Onboarding:Worker:ReviewMaxInterval"] = "00:00:00.500",
        }));

    private async Task DropDatabasesAsync(SqlOptions sql)
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(SqlConnections.For(sql, "master"));
        await connection.OpenAsync();

        var names = new List<string>();
        await using (var query = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @prefix", connection))
        {
            query.Parameters.AddWithValue("@prefix", _prefix + "[_]%");
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            var quoted = "[" + name.Replace("]", "]]") + "]";
            await using var drop = new SqlCommand($"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AtlasCollection : ICollectionFixture<AtlasFixture>
{
    public const string Name = "atlas";
}
