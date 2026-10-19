using System.Collections.Concurrent;
using Atlas.Shared.Markets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Persistence;

public sealed class SqlOptions
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>Databases are named {prefix}_{service}_{market}. Tests use a unique prefix per run.</summary>
    public string DatabasePrefix { get; set; } = "atlas";
}

/// <summary>
/// One database per market per service (GC-2026-0814 #1). Locally they share the compose SQL Server; in production
/// each market's deployment points at a server in that country.
/// </summary>
public interface IMarketDbContextFactory<out TContext>
    where TContext : DbContext
{
    TContext Create(string market);

    string DatabaseName(string market);
}

internal sealed class MarketDbContextFactory<TContext>(
    IOptions<SqlOptions> sql,
    string service,
    Func<DbContextOptions<TContext>, TContext> create) : IMarketDbContextFactory<TContext>
    where TContext : DbContext
{
    private readonly ConcurrentDictionary<string, DbContextOptions<TContext>> _options = new(StringComparer.OrdinalIgnoreCase);

    public TContext Create(string market) => create(_options.GetOrAdd(market, BuildOptions));

    public string DatabaseName(string market) => $"{sql.Value.DatabasePrefix}_{service}_{market.ToLowerInvariant()}";

    private DbContextOptions<TContext> BuildOptions(string market) =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(SqlConnections.For(sql.Value, DatabaseName(market)))
            .Options;
}

public static class SqlConnections
{
    public static string For(SqlOptions sql, string database) => new SqlConnectionStringBuilder
    {
        DataSource = $"{sql.Server},{sql.Port}",
        InitialCatalog = database,
        UserID = sql.User,
        Password = sql.Password,
        TrustServerCertificate = true,
        ConnectTimeout = 10,
    }.ConnectionString;
}

public static class MarketDatabaseExtensions
{
    public static IServiceCollection AddMarketDatabases<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string service,
        Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        services.AddOptions<SqlOptions>().Bind(configuration.GetSection("Atlas:Sql"));
        services.AddSingleton<IMarketDbContextFactory<TContext>>(sp =>
            new MarketDbContextFactory<TContext>(sp.GetRequiredService<IOptions<SqlOptions>>(), service, create));
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            $"sql-{service}",
            sp => new MarketDatabasesHealthCheck<TContext>(
                sp.GetRequiredService<IMarketCatalog>(),
                sp.GetRequiredService<IMarketDbContextFactory<TContext>>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]));
        return services;
    }

    /// <summary>
    /// Creates or migrates every market's database before the service starts serving. EF Core 10 takes a lock while
    /// migrating, so replicas starting together are safe. In production this would run once per deployment instead,
    /// so the services' own database identity needs no rights to change the schema.
    /// </summary>
    public static async Task MigrateMarketDatabasesAsync<TContext>(this WebApplication app, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        var markets = app.Services.GetRequiredService<IMarketCatalog>();
        var factory = app.Services.GetRequiredService<IMarketDbContextFactory<TContext>>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Startup");

        await Task.WhenAll(markets.All.Select(async market =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var db = factory.Create(market.Code);
                    await db.Database.MigrateAsync(cancellationToken);
                    logger.LogInformation("Database {Database} is up to date", factory.DatabaseName(market.Code));
                    return;
                }
                catch (SqlException) when (attempt < 10)
                {
                    // SQL Server can report healthy a moment before it accepts logins.
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }));
    }
}

/// <summary>Reports each market's database separately, so /health/ready shows which market is down.</summary>
internal sealed class MarketDatabasesHealthCheck<TContext>(IMarketCatalog markets, IMarketDbContextFactory<TContext> factory) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(markets.All.Select(async market =>
        {
            await using var db = factory.Create(market.Code);
            bool reachable;
            try
            {
                reachable = await db.Database.CanConnectAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                reachable = false;
            }

            return (Name: factory.DatabaseName(market.Code), Reachable: reachable);
        }));

        var data = results.ToDictionary(r => r.Name, r => (object)(r.Reachable ? "reachable" : "unreachable"));
        return results.All(r => r.Reachable)
            ? HealthCheckResult.Healthy("Every market database is reachable.", data)
            : HealthCheckResult.Unhealthy("A market database is unreachable.", data: data);
    }
}
