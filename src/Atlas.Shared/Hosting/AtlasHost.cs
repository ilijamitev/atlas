using System.Text.Json;
using Atlas.Shared.Auth;
using Atlas.Shared.Markets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace Atlas.Shared.Hosting;

/// <summary>What every Atlas service starts from: shared settings, logging, problem details, auth, health, OpenAPI.</summary>
public static class AtlasHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string serviceName)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddAtlasSettings();
        builder.AddAtlasLogging(serviceName);

        builder.Services.AddSingleton(new ServiceIdentity(serviceName));
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<OpenApiSecurity>();
            options.AddOperationTransformer<OpenApiSecurity>();
        });
        builder.Services.AddHealthChecks();
        builder.Services.AddAtlasAuth(builder.Configuration);
        builder.Services.AddAtlasMarkets(builder.Configuration);
        return builder;
    }

    public static WebApplication UseAtlasDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteHealthAsync });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = WriteHealthAsync });

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName));
        }

        return app;
    }

    /// <summary>
    /// The shared atlas.json goes underneath everything else, so a service's appsettings, environment variables and
    /// tests can override it. The compose file's port variables come last: one SQL_PORT, SEQ_PORT or BLOB_PORT moves
    /// the containers and the services together.
    /// </summary>
    public static void AddAtlasSettings(this IConfigurationManager configuration)
    {
        configuration.Sources.Insert(0, new JsonConfigurationSource
        {
            Path = "atlas.json",
            Optional = false,
            FileProvider = new PhysicalFileProvider(AppContext.BaseDirectory),
        });

        var overrides = new Dictionary<string, string?>();
        if (Environment.GetEnvironmentVariable("SQL_PORT") is { Length: > 0 } sqlPort)
        {
            overrides["Atlas:Sql:Port"] = sqlPort;
        }

        if (Environment.GetEnvironmentVariable("SEQ_PORT") is { Length: > 0 } seqPort)
        {
            overrides["Atlas:Seq:Url"] = $"http://localhost:{seqPort}";
        }

        if (Environment.GetEnvironmentVariable("BLOB_PORT") is { Length: > 0 } blobPort)
        {
            overrides["Atlas:Blob:Endpoint"] = $"http://127.0.0.1:{blobPort}/devstoreaccount1";
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }
    }

    /// <summary>
    /// A logger per host rather than Serilog's static bootstrap logger, so the integration tests can run all three
    /// services in one process. Logs carry IDs, statuses and outcomes only: never personal data.
    /// </summary>
    private static void AddAtlasLogging(this WebApplicationBuilder builder, string serviceName) =>
        builder.Services.AddSerilog(
            (services, logger) =>
            {
                logger.MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Service", serviceName)
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}: {Message:lj}{NewLine}{Exception}");

                var seqUrl = services.GetRequiredService<IConfiguration>()["Atlas:Seq:Url"];
                if (!string.IsNullOrWhiteSpace(seqUrl))
                {
                    logger.WriteTo.Seq(seqUrl);
                }
            },
            preserveStaticLogger: true);

    private static readonly JsonSerializerOptions HealthJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static Task WriteHealthAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    durationMs = (int)entry.Value.Duration.TotalMilliseconds,
                    details = entry.Value.Data,
                }),
            },
            HealthJson);
}
