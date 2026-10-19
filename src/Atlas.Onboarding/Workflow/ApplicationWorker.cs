using Atlas.Onboarding.Documents;
using Atlas.Shared.Markets;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Workflow;

/// <summary>
/// The one durable worker. Every instance runs one: it claims due applications in every market it serves, processes
/// them in parallel up to a limit, sweeps abandoned uploads now and then, and sleeps until the next poll or a submit
/// wakes it.
/// </summary>
internal sealed class ApplicationWorker(
    IMarketCatalog markets,
    WorkClaimer claimer,
    ApplicationSteps steps,
    DocumentSweeper sweeper,
    WorkerSignal signal,
    IOptions<OnboardingOptions> options,
    ILogger<ApplicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await RunOnceAsync(stoppingToken);
                await sweeper.SweepIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The worker loop failed; it tries again on the next poll");
            }

            if (claimed > 0)
            {
                continue;
            }

            try
            {
                await signal.WaitAsync(options.Value.Worker.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over every market. Returns how many applications it claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = new List<(string Market, Guid Id)>();
        foreach (var market in markets.All)
        {
            try
            {
                claimed.AddRange((await claimer.ClaimAsync(market.Code, cancellationToken)).Select(id => (market.Code, id)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One market's database being down mustn't stop the others.
                logger.LogWarning(ex, "Claiming work in {Market} failed", market.Code);
            }
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.Worker.MaxParallel), CancellationToken = cancellationToken },
            async (item, token) =>
            {
                try
                {
                    await steps.RunAsync(item.Market, item.Id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The lease runs out and the application is picked up again later.
                    logger.LogError(ex, "Processing application {ApplicationId} in {Market} failed", item.Id, item.Market);
                }
            });
        return claimed.Count;
    }
}
