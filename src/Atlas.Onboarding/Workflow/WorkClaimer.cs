using System.Data;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Workflow;

/// <summary>
/// Claims due applications so that three replicas never work on the same one. UPDLOCK stops two claims taking the
/// same row, READPAST makes each replica skip rows another is claiming, and the lease keeps the row reserved while
/// its step runs. If an instance dies, the lease runs out and another replica takes over; RowVersion catches anything
/// that still overlaps.
/// </summary>
internal sealed class WorkClaimer(IMarketDbContextFactory<OnboardingDbContext> databases, IOptions<OnboardingOptions> options, TimeProvider time)
{
    private const string ClaimSql = """
        WITH due AS (
            SELECT TOP (@batch) Id, LeaseUntil
            FROM Applications WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE NextAttemptAt IS NOT NULL AND NextAttemptAt <= @now
              AND (LeaseUntil IS NULL OR LeaseUntil < @now)
            ORDER BY NextAttemptAt
        )
        UPDATE due SET LeaseUntil = @leaseUntil
        OUTPUT inserted.Id AS [Value];
        """;

    public async Task<IReadOnlyList<Guid>> ClaimAsync(string market, CancellationToken cancellationToken)
    {
        var worker = options.Value.Worker;
        var now = time.GetUtcNow();
        await using var db = databases.Create(market);
        return await db.Database
            .SqlQueryRaw<Guid>(
                ClaimSql,
                new SqlParameter("@batch", SqlDbType.Int) { Value = worker.BatchSize },
                new SqlParameter("@now", SqlDbType.DateTimeOffset) { Value = now },
                new SqlParameter("@leaseUntil", SqlDbType.DateTimeOffset) { Value = now + worker.Lease })
            .ToListAsync(cancellationToken);
    }
}
