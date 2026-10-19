using Atlas.Onboarding.Persistence;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Applications;

/// <summary>
/// The bounded wait behind "one call in, one answer out": after a submit, keep the request open until the application
/// leaves the worker's states, for at most <see cref="OnboardingOptions.DecisionWait"/>. It reads the database rather
/// than waiting for an in-process signal, because another replica's worker may be the one doing the work. A parked
/// application answers at once: waiting for an outage to end helps nobody.
/// </summary>
internal sealed class DecisionWaiter(IMarketDbContextFactory<OnboardingDbContext> databases, IOptions<OnboardingOptions> options, TimeProvider time)
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(200);

    public async Task<Application?> WaitAsync(string market, Guid applicationId, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + options.Value.DecisionWait;
        while (true)
        {
            // The status alone: the applicant and documents aren't needed once an application is past Draft.
            Application? application;
            await using (var db = databases.Create(market))
            {
                application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
            }

            var settled = application is null
                || !application.IsInProgress
                || application.IsRestricted
                || application.NeedsAttention
                || application.LastError is not null;
            if (settled || time.GetUtcNow() >= deadline)
            {
                return application;
            }

            await Task.Delay(PollEvery, time, cancellationToken);
        }
    }
}
