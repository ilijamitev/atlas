using Atlas.Shared.Encryption;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Documents;

/// <summary>Where document images live. Images go in encrypted with the market's key and come out decrypted.</summary>
public interface IDocumentStore
{
    Task UploadAsync(string market, string blobName, byte[] image, CancellationToken cancellationToken);

    Task<byte[]> DownloadAsync(string market, string blobName, CancellationToken cancellationToken);

    /// <summary>Deletes the blob if it exists; deleting a missing blob is not an error, so a retry can finish the job.</summary>
    Task DeleteAsync(string market, string blobName, CancellationToken cancellationToken);
}

/// <summary>
/// Blob storage settings. Locally, Azurite with its well-known development account; in production, a storage account in
/// each market's country, reached with a managed identity instead of a key.
/// </summary>
public sealed class BlobOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:10000/devstoreaccount1";
    public string AccountName { get; set; } = "devstoreaccount1";
    public string AccountKey { get; set; } = "";

    /// <summary>Containers are named {prefix}-{market}, e.g. documents-mb. Tests use a unique prefix per run.</summary>
    public string ContainerPrefix { get; set; } = "documents";

    public string ContainerName(string market) => $"{ContainerPrefix}-{market.ToLowerInvariant()}";
}

/// <summary>One private container per market (GC-2026-0814 #1). Blob names are GUIDs and carry no personal data.</summary>
internal sealed class BlobDocumentStore(BlobServiceClient blobs, IOptions<BlobOptions> options, IFieldCipher cipher) : IDocumentStore
{
    public Task UploadAsync(string market, string blobName, byte[] image, CancellationToken cancellationToken) =>
        Classified(async () =>
        {
            var encrypted = cipher.Encrypt(market, image);
            await Blob(market, blobName).UploadAsync(BinaryData.FromBytes(encrypted), overwrite: true, cancellationToken);
        });

    public async Task<byte[]> DownloadAsync(string market, string blobName, CancellationToken cancellationToken)
    {
        byte[] encrypted = [];
        await Classified(async () =>
        {
            var content = await Blob(market, blobName).DownloadContentAsync(cancellationToken);
            encrypted = content.Value.Content.ToArray();
        });
        return cipher.Decrypt(market, encrypted);
    }

    public Task DeleteAsync(string market, string blobName, CancellationToken cancellationToken) =>
        Classified(() => Blob(market, blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken));

    private BlobClient Blob(string market, string blobName) =>
        blobs.GetBlobContainerClient(options.Value.ContainerName(market)).GetBlobClient(blobName);

    private static async Task Classified(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (RequestFailedException ex) when (ex.Status is 0 or 408 or 429 or >= 500)
        {
            throw new TransientFailureException("blob", ex.Status == 0 ? "unreachable" : $"HTTP {ex.Status}", ex);
        }
        catch (RequestFailedException ex)
        {
            throw new PermanentFailureException("blob", $"HTTP {ex.Status}");
        }
    }
}

internal static class DocumentStorageExtensions
{
    public static IServiceCollection AddDocumentStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BlobOptions>().Bind(configuration.GetSection("Atlas:Blob"));
        services.AddSingleton(sp =>
        {
            var blob = sp.GetRequiredService<IOptions<BlobOptions>>().Value;
            var clientOptions = new BlobClientOptions
            {
                Retry = { MaxRetries = 2, Delay = TimeSpan.FromMilliseconds(500), Mode = RetryMode.Exponential, NetworkTimeout = TimeSpan.FromSeconds(10) },
            };
            return new BlobServiceClient(new Uri(blob.Endpoint), new StorageSharedKeyCredential(blob.AccountName, blob.AccountKey), clientOptions);
        });
        services.AddSingleton<IDocumentStore, BlobDocumentStore>();
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "blob-documents",
            sp => new DocumentContainersHealthCheck(
                sp.GetRequiredService<BlobServiceClient>(),
                sp.GetRequiredService<IOptions<BlobOptions>>(),
                sp.GetRequiredService<IMarketCatalog>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]));
        return services;
    }

    /// <summary>Creates every market's private container before the service starts, like the database migrations.</summary>
    public static async Task EnsureDocumentContainersAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var blobs = app.Services.GetRequiredService<BlobServiceClient>();
        var options = app.Services.GetRequiredService<IOptions<BlobOptions>>().Value;
        var markets = app.Services.GetRequiredService<IMarketCatalog>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Startup");

        foreach (var market in markets.All)
        {
            var name = options.ContainerName(market.Code);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await blobs.GetBlobContainerClient(name).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
                    logger.LogInformation("Blob container {Container} is ready", name);
                    break;
                }
                catch (RequestFailedException) when (attempt < 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
    }
}

/// <summary>Reports each market's container separately, like the databases.</summary>
internal sealed class DocumentContainersHealthCheck(BlobServiceClient blobs, IOptions<BlobOptions> options, IMarketCatalog markets) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(markets.All.Select(async market =>
        {
            var name = options.Value.ContainerName(market.Code);
            string state;
            try
            {
                state = (await blobs.GetBlobContainerClient(name).ExistsAsync(cancellationToken)).Value ? "reachable" : "missing";
            }
            catch (RequestFailedException)
            {
                state = "unreachable";
            }

            return (Name: name, State: state);
        }));

        var data = results.ToDictionary(r => r.Name, r => (object)r.State);
        return results.All(r => r.State == "reachable")
            ? HealthCheckResult.Healthy("Every market's document container is reachable.", data)
            : HealthCheckResult.Unhealthy("A market's document container is missing or unreachable.", data: data);
    }
}
