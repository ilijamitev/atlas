using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Documents;

/// <summary>
/// Removes document rows whose upload never finished, and their blob if one got written, so no image is ever left
/// without a record. The blob goes first, then the row: if the delete fails halfway, the next sweep finishes it.
/// </summary>
internal sealed class DocumentSweeper(
    IMarketDbContextFactory<OnboardingDbContext> databases,
    IMarketCatalog markets,
    IDocumentStore store,
    IOptions<OnboardingOptions> options,
    TimeProvider time,
    ILogger<DocumentSweeper> logger)
{
    private const int BatchSize = 100;

    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    public async Task SweepIfDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now + options.Value.Worker.SweepInterval;
        foreach (var market in markets.All)
        {
            try
            {
                await SweepAsync(market.Code, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sweeping pending documents in {Market} failed; the next sweep tries again", market.Code);
            }
        }
    }

    public async Task<int> SweepAsync(string market, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - options.Value.Worker.PendingDocumentTimeout;
        await using var db = databases.Create(market);
        var abandoned = await db.Documents
            .Where(d => d.State == DocumentState.Pending && d.CreatedAt < cutoff)
            .OrderBy(d => d.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var document in abandoned)
        {
            await store.DeleteAsync(market, document.BlobName, cancellationToken);
            db.Documents.Remove(document);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (abandoned.Count > 0)
        {
            logger.LogInformation("Removed {Count} abandoned document uploads in {Market}", abandoned.Count, market);
        }

        return abandoned.Count;
    }
}
