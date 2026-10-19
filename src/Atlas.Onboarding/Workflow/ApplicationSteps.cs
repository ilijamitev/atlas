using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Clients;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Auth;
using Atlas.Shared.Contracts;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Workflow;

/// <summary>
/// Moves one claimed application on, one step at a time: load, call one provider, apply the answer, save. Each step
/// is saved before the next starts, so a crash resumes from the last saved step. A technical failure parks the
/// application with a backoff; a permanent one flags it for operations. Neither changes its state.
/// </summary>
internal sealed class ApplicationSteps(
    IMarketDbContextFactory<OnboardingDbContext> databases,
    IMarketCatalog markets,
    IdNowClient idNow,
    ScreeningClient screening,
    CoreBankingClient coreBanking,
    IDocumentStore documents,
    IApplicantNotifier notifier,
    ServiceIdentity identity,
    IOptions<OnboardingOptions> options,
    TimeProvider time,
    ILogger<ApplicationSteps> logger)
{
    private const int MaxStepsPerClaim = 8;

    private WorkerOptions Worker => options.Value.Worker;

    public async Task RunAsync(string marketCode, Guid applicationId, CancellationToken cancellationToken)
    {
        if (!markets.TryGet(marketCode, out var market))
        {
            return;
        }

        for (var step = 0; step < MaxStepsPerClaim; step++)
        {
            await using var db = databases.Create(market.Code);
            var application = await db.Applications
                .Include(a => a.Applicant)
                .Include(a => a.Documents)
                .SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);
            if (application is null || application.IsRestricted || application.NextAttemptAt > time.GetUtcNow() || application.NextAttemptAt is null)
            {
                return;
            }

            var stateBefore = application.State;
            var statusBefore = CustomerStatus.Of(application);
            try
            {
                await StepAsync(db, market, application, cancellationToken);
            }
            catch (TransientFailureException ex)
            {
                var delay = new RetryBackoff(Worker.RetryBaseDelay, Worker.RetryMaxDelay).Delay(application.RetryCount + 1, Random.Shared.NextDouble());
                application.Park($"{ex.Target}: {ex.Reason}", delay, time.GetUtcNow());
                logger.LogWarning(
                    "Application {ApplicationId} in {Market} parked in {State} after {Target} failed ({Reason}); retry {Retry} in {DelaySeconds:0.0} s",
                    application.Id, market.Code, application.State, ex.Target, ex.Reason, application.RetryCount, delay.TotalSeconds);
            }
            catch (PermanentFailureException ex)
            {
                application.FlagForOperations($"{ex.Target}: {ex.Reason}", time.GetUtcNow());
                logger.LogError(
                    "Application {ApplicationId} in {Market} needs operations: {Target} refused in {State} ({Reason})",
                    application.Id, market.Code, ex.Target, application.State, ex.Reason);
            }

            // Keep the claim while there is more to do right now, and let it go otherwise.
            var now = time.GetUtcNow();
            var continuing = application.NextAttemptAt <= now;
            application.LeaseUntil = continuing ? now + Worker.Lease : null;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Application {ApplicationId} in {Market} changed while it was being processed; leaving it", applicationId, market.Code);
                return;
            }

            Report(market.Code, application, stateBefore, statusBefore);
            if (!continuing)
            {
                return;
            }
        }
    }

    private Task StepAsync(OnboardingDbContext db, Market market, Application application, CancellationToken cancellationToken) =>
        application.State switch
        {
            ApplicationState.Verifying => VerifyAsync(db, market, application, cancellationToken),
            ApplicationState.Screening => ScreenAsync(db, market, application, cancellationToken),
            ApplicationState.UnderReview => CheckReviewAsync(market, application, cancellationToken),
            ApplicationState.OpeningAccount => OpenAccountAsync(db, market, application, cancellationToken),
            _ => throw new PermanentFailureException("worker", $"nothing to do in {application.State}"),
        };

    /// <summary>IDNow first, so a forged document or a face mismatch never reaches World-Check or an officer.</summary>
    private async Task VerifyAsync(OnboardingDbContext db, Market market, Application application, CancellationToken cancellationToken)
    {
        var document = application.CurrentIdentityDocument ?? throw new PermanentFailureException("documents", "no identity document");
        var selfie = application.CurrentSelfie ?? throw new PermanentFailureException("documents", "no selfie");

        db.LogAccess(Actor.System(identity.Name), AccessAction.Read, "application", application.Id.ToString(), time.GetUtcNow());
        var documentImage = await documents.DownloadAsync(market.Code, document.BlobName, cancellationToken);
        var selfieImage = await documents.DownloadAsync(market.Code, selfie.BlobName, cancellationToken);
        var result = await idNow.IdentifyAsync(application.Id, application.RetakeCount, document.Type, documentImage, selfieImage, cancellationToken);

        var now = time.GetUtcNow();
        application.RecordProviderResult(
            IdNowClient.Name,
            result.IdentificationId,
            $"{result.DocumentResult}, face match {result.FaceMatch}, confidence {result.Confidence:0.00}",
            now);
        application.RecordIdentification(result, market.Options, now);
    }

    private async Task ScreenAsync(OnboardingDbContext db, Market market, Application application, CancellationToken cancellationToken)
    {
        var applicant = application.Applicant;
        var subject = new ScreeningRequest(
            applicant.FirstName!, applicant.LastName!, applicant.DateOfBirth!.Value, applicant.Nationality!, application.SubmittedAt!.Value);

        db.LogAccess(Actor.System(identity.Name), AccessAction.Read, "application", application.Id.ToString(), time.GetUtcNow());
        var status = await screening.ScreenAsync(market.Code, application.Id, subject, cancellationToken);

        var now = time.GetUtcNow();
        application.RecordProviderResult(ScreeningClient.Name, null, status, now);
        application.RecordScreening(status, market.Options, ReviewSchedule(), now);
    }

    /// <summary>Only the status crosses over; Onboarding has no way to clear a match itself.</summary>
    private async Task CheckReviewAsync(Market market, Application application, CancellationToken cancellationToken)
    {
        var status = await screening.StatusAsync(market.Code, application.Id, cancellationToken);
        var now = time.GetUtcNow();
        if (status != ScreeningStatus.Referred)
        {
            application.RecordProviderResult(ScreeningClient.Name, null, $"{status} by an officer", now);
        }

        application.RecordScreening(status, market.Options, ReviewSchedule(), now);
    }

    /// <summary>Account, then card. Both are keyed by application ID, so repeating the step after a failure is safe.</summary>
    private async Task OpenAccountAsync(OnboardingDbContext db, Market market, Application application, CancellationToken cancellationToken)
    {
        if (application.AccountReference is null)
        {
            db.LogAccess(Actor.System(identity.Name), AccessAction.Read, "application", application.Id.ToString(), time.GetUtcNow());
            var account = await coreBanking.OpenAccountAsync(market.Code, application.Id, application.Applicant, cancellationToken);
            application.RecordAccount(account);
            application.RecordProviderResult(CoreBankingClient.Name, account, "account opened", time.GetUtcNow());
        }

        var card = await coreBanking.OrderCardAsync(application.Id, application.AccountReference!, cancellationToken);
        var now = time.GetUtcNow();
        application.RecordProviderResult(CoreBankingClient.Name, card, "card ordered", now);
        application.RecordCard(card, now);
    }

    private ReviewSchedule ReviewSchedule() => new(Worker.ReviewFirstCheck, Worker.ReviewMaxInterval);

    private void Report(string market, Application application, ApplicationState stateBefore, string statusBefore)
    {
        if (application.State != stateBefore)
        {
            logger.LogInformation(
                "Application {ApplicationId} in {Market}: {From} -> {To}", application.Id, market, stateBefore, application.State);
        }

        if (application.IsFinal && stateBefore != application.State && application.SubmittedAt is { } submitted && application.DecidedAt is { } decided)
        {
            // AC2 is measured here, per market, from submission to decision.
            logger.LogInformation(
                "Application {ApplicationId} in {Market} decided {Decision} {DecisionMs:0} ms after submission",
                application.Id, market, application.State, (decided - submitted).TotalMilliseconds);
        }

        var status = CustomerStatus.Of(application);
        if (status != statusBefore)
        {
            notifier.StatusChanged(market, application.Id, status);
        }
    }
}
