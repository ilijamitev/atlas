using System.Security.Claims;
using System.Security.Cryptography;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Onboarding.Workflow;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Encryption;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atlas.Onboarding.Applications;

/// <summary>A document row saved as <c>Pending</c>, with the image still to upload.</summary>
internal sealed record PendingUpload(Document Document, byte[] Bytes);

/// <summary>
/// What applicants and branch staff can do. Each operation loads the application, changes it through the aggregate,
/// writes the access-log row in the same save, and answers with a neutral status. Document images are written
/// row-first: the row as <c>Pending</c>, then the blob, then the row as <c>Stored</c>.
/// </summary>
internal sealed class ApplicationService(
    IMarketDbContextFactory<OnboardingDbContext> databases,
    IMarketCatalog markets,
    IFieldCipher cipher,
    IDocumentStore documents,
    WorkerSignal worker,
    DecisionWaiter waiter,
    IOptions<OnboardingOptions> options,
    TimeProvider time,
    ILogger<ApplicationService> logger)
{
    private const int MaxConcurrencyRetries = 3;

    public async Task<IResult> CreateAsync(string marketCode, string? idempotencyKey, HttpRequest request, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        var key = idempotencyKey?.Trim();
        if (key is not { Length: > 0 and <= 100 })
        {
            return Problems.Validation([new FieldError("Idempotency-Key", "idempotencyKey.required", "Send an Idempotency-Key header (up to 100 characters), new for each application.")]);
        }

        var (body, bodyError) = await RequestBody.ReadAsync(request, options.Value.MaxRequestBytes, cancellationToken);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var (input, parseError) = RequestBody.Parse(body!);
        if (parseError is not null)
        {
            return parseError;
        }

        var requestHash = SHA256.HashData(body!);
        var now = time.GetUtcNow();
        var errors = ApplicationRules.Validate(input!, market, Today(now), options.Value.MaxImageBytes, out var images);

        await using var db = databases.Create(market.Code);
        if (await LoadByKeyAsync(db, key, cancellationToken) is { } existing)
        {
            return await ReplayAsync(db, market, existing, requestHash, images, cancellationToken);
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var token = ApplicationTokens.New();
        var applicant = new Applicant();
        ApplicationRules.Apply(applicant, input!, market.Code, cipher, now);
        var application = Application.Start(applicant, key, requestHash, ApplicationTokens.Hash(token), now);

        // EF assigns the GUIDs (sequential for SQL Server) as the entities are added, so the blob names can use them.
        db.Applications.Add(application);
        var uploads = AddDocuments(db, application, images, now);
        db.LogAccess(Actor.Applicant(application.Id), AccessAction.Create, "application", application.Id.ToString(), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // The same key arrived twice at the same moment, and the other request created the application.
            await using var fresh = databases.Create(market.Code);
            return await LoadByKeyAsync(fresh, key, cancellationToken) is { } winner
                ? await ReplayAsync(fresh, market, winner, requestHash, images, cancellationToken)
                : Changed();
        }

        logger.LogInformation("Application {ApplicationId} created in {Market}", application.Id, market.Code);
        return await FinishCreateAsync(db, market, application, uploads, token, cancellationToken);
    }

    public async Task<IResult> GetAsync(string marketCode, Guid applicationId, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        await using var db = databases.Create(market.Code);
        var application = await LoadAsync(db, applicationId, cancellationToken);
        if (application is null)
        {
            return NotFound();
        }

        // Logged before the data leaves; if the row can't be written, nothing is returned.
        db.LogAccess(Actor.Applicant(applicationId), AccessAction.Read, "application", applicationId.ToString(), time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        var identifier = Identifier(market, application);
        return TypedResults.Ok(ApplicationViews.Details(application, identifier is null ? null : ApplicationViews.Mask(identifier)));
    }

    public async Task<IResult> PatchAsync(string marketCode, Guid applicationId, HttpRequest request, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        var (body, bodyError) = await RequestBody.ReadAsync(request, options.Value.MaxRequestBytes, cancellationToken);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var (input, parseError) = RequestBody.Parse(body!);
        if (parseError is not null)
        {
            return parseError;
        }

        await using var db = databases.Create(market.Code);
        var application = await LoadAsync(db, applicationId, cancellationToken);
        if (application is null)
        {
            return NotFound();
        }

        if (application.IsRestricted)
        {
            return Withdrawn();
        }

        if (application.State is not (ApplicationState.Draft or ApplicationState.ActionRequired))
        {
            return Problems.Of(StatusCodes.Status409Conflict, "application.notEditable", "The application has been submitted and can't be changed.");
        }

        if (application.State == ApplicationState.ActionRequired && input!.ChangesDetails)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "application.onlyPhotos", "Only new photos can be sent now.");
        }

        var now = time.GetUtcNow();
        var errors = ApplicationRules.Validate(input!, market, Today(now), options.Value.MaxImageBytes, out var images);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        ApplicationRules.Apply(application.Applicant, input!, market.Code, cipher, now);
        var uploads = AddDocuments(db, application, images, now);
        application.Touch(now);
        db.LogAccess(Actor.Applicant(applicationId), AccessAction.Amend, "application", applicationId.ToString(), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            return Changed();
        }

        return await UploadAsync(db, market, uploads, cancellationToken) ?? TypedResults.Ok(ApplicationViews.Status(application));
    }

    public async Task<IResult> SubmitAsync(string marketCode, Guid applicationId, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        await using var db = databases.Create(market.Code);
        var application = await LoadAsync(db, applicationId, cancellationToken);
        if (application is null)
        {
            return NotFound();
        }

        if (application.IsRestricted)
        {
            return Withdrawn();
        }

        var now = time.GetUtcNow();
        switch (application.State)
        {
            case ApplicationState.Draft when ApplicationRules.Missing(application) is { Count: > 0 } missing:
                return Problems.Validation(missing.Select(field => new FieldError(field, $"{field}.required", "Required before submitting.")));
            case ApplicationState.ActionRequired when !application.HasPhotoSinceRetakeRequest():
                return Problems.Validation([new FieldError("documents", "documents.retakeNeeded", "Send a new photo before submitting again.")]);
            case ApplicationState.Draft or ApplicationState.ActionRequired:
                application.Submit(now);
                db.LogAccess(Actor.Applicant(applicationId), AccessAction.Amend, "application", applicationId.ToString(), now);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Changed();
                }

                logger.LogInformation("Application {ApplicationId} submitted in {Market}", applicationId, market.Code);
                worker.Wake();
                break;
        }

        // Submitting again after a submit just reports where the application is.
        return await WaitAndRespondAsync(market, application, cancellationToken);
    }

    /// <summary>
    /// GC #5. A draft that was never submitted is deleted at once, images first, then rows. A submitted application is
    /// restricted instead: processing stops, and deletion waits for the end of the ten-year retention (GC #4).
    /// </summary>
    public async Task<IResult> EraseAsync(string marketCode, Guid applicationId, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = databases.Create(market.Code);
            var application = await LoadAsync(db, applicationId, cancellationToken);
            if (application is null)
            {
                return NotFound();
            }

            var now = time.GetUtcNow();
            var deleting = !application.IsSubmitted;
            if (deleting)
            {
                try
                {
                    foreach (var document in application.Documents)
                    {
                        await documents.DeleteAsync(market.Code, document.BlobName, cancellationToken);
                    }
                }
                catch (TransientFailureException)
                {
                    return StorageUnavailable();
                }

                db.Applications.Remove(application);
            }
            else
            {
                application.Restrict(now);
            }

            db.LogAccess(Actor.Applicant(applicationId), AccessAction.Erase, "application", applicationId.ToString(), now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                continue;
            }
            catch (DbUpdateConcurrencyException)
            {
                return Changed();
            }

            if (deleting)
            {
                logger.LogInformation("Draft {ApplicationId} in {Market} erased with its documents", applicationId, market.Code);
                return TypedResults.Ok(new ErasureResponse(applicationId, "DELETED", null));
            }

            logger.LogInformation("Application {ApplicationId} in {Market} restricted on an erasure request", applicationId, market.Code);
            return TypedResults.Accepted((string?)null, new ErasureResponse(applicationId, "RESTRICTED", application.RetentionUntil));
        }
    }

    /// <summary>What branch staff check against the physical ID: the details, with the identifier in full. Logged.</summary>
    public async Task<IResult> BranchViewAsync(string marketCode, Guid applicationId, ClaimsPrincipal staff, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        await using var db = databases.Create(market.Code);
        var application = await LoadAsync(db, applicationId, cancellationToken);
        if (application is null || !application.IsSubmitted)
        {
            return NotFound();
        }

        db.LogAccess(Actor.From(staff), AccessAction.Read, "application", applicationId.ToString(), time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ApplicationViews.Details(application, Identifier(market, application)));
    }

    /// <summary>Annex B, MD: only after an in-person ID check and a wet signature is the account opened.</summary>
    public async Task<IResult> ActivateAsync(
        string marketCode, Guid applicationId, BranchActivationRequest request, ClaimsPrincipal staff, CancellationToken cancellationToken)
    {
        var market = Market(marketCode);
        if (!request.IdentityDocumentChecked || !request.WetSignatureCollected)
        {
            return Problems.Validation([new FieldError("confirmations", "branch.confirmationsRequired", "Confirm both the in-person ID check and the wet signature.")]);
        }

        await using var db = databases.Create(market.Code);
        var application = await LoadAsync(db, applicationId, cancellationToken);
        if (application is null || !application.IsSubmitted)
        {
            return NotFound();
        }

        if (application.IsRestricted)
        {
            return Withdrawn();
        }

        if (application.State != ApplicationState.AwaitingBranchVisit)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "application.notAwaitingBranchVisit", "The application isn't waiting for a branch visit.");
        }

        var now = time.GetUtcNow();
        var actor = Actor.From(staff);
        application.ActivateInBranch(actor.Id, now);
        db.LogAccess(actor, AccessAction.Amend, "application", applicationId.ToString(), now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Changed();
        }

        logger.LogInformation("Application {ApplicationId} in {Market} activated in branch", applicationId, market.Code);
        worker.Wake();
        return await WaitAndRespondAsync(market, application, cancellationToken);
    }

    /// <summary>
    /// The same key and the same body again: the app lost our first answer. It gets the same application with a new
    /// token (only the first one's hash was kept), and whatever the first attempt didn't finish is finished now.
    /// </summary>
    private async Task<IResult> ReplayAsync(
        OnboardingDbContext db, Market market, Application existing, byte[] requestHash, List<DecodedImage> images, CancellationToken cancellationToken)
    {
        if (!existing.RequestHash.AsSpan().SequenceEqual(requestHash))
        {
            return Problems.Of(
                StatusCodes.Status422UnprocessableEntity,
                "idempotencyKey.reused",
                "This Idempotency-Key was already used for a different request. Use a new key for a new application.");
        }

        var token = ApplicationTokens.New();
        existing.TokenHash = ApplicationTokens.Hash(token);
        existing.Touch(time.GetUtcNow());
        var unfinished = existing.Documents
            .Where(d => d.State == DocumentState.Pending)
            .Select(d => images.FirstOrDefault(i => i.Type == d.Type && i.Sha256.AsSpan().SequenceEqual(d.Sha256)) is { } image ? new PendingUpload(d, image.Bytes) : null)
            .OfType<PendingUpload>()
            .ToList();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Changed();
        }

        logger.LogInformation("Create replayed for application {ApplicationId} in {Market}; token reissued", existing.Id, market.Code);
        return await FinishCreateAsync(db, market, existing, unfinished, token, cancellationToken);
    }

    private async Task<IResult> FinishCreateAsync(
        OnboardingDbContext db, Market market, Application application, List<PendingUpload> uploads, string token, CancellationToken cancellationToken)
    {
        if (await UploadAsync(db, market, uploads, cancellationToken) is { } problem)
        {
            return problem;
        }

        // A complete body submits in the same call, so the ticket's single POST still works.
        if (application.State == ApplicationState.Draft && ApplicationRules.Missing(application).Count == 0)
        {
            var now = time.GetUtcNow();
            application.Submit(now);
            db.LogAccess(Actor.Applicant(application.Id), AccessAction.Amend, "application", application.Id.ToString(), now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Changed();
            }

            logger.LogInformation("Application {ApplicationId} submitted in {Market}", application.Id, market.Code);
            worker.Wake();
        }

        var current = application.IsInProgress ? await waiter.WaitAsync(market.Code, application.Id, cancellationToken) ?? application : application;
        return TypedResults.Created(Location(market, application.Id), ApplicationViews.Created(current, token));
    }

    /// <summary>
    /// Uploads each image and marks its row <c>Stored</c>. If storage is unavailable, the rest stay <c>Pending</c> for a
    /// retry or the sweep. If the rows vanished meanwhile (an erasure won the race), the images just uploaded are
    /// deleted again, so none is left without a record.
    /// </summary>
    private async Task<IResult?> UploadAsync(OnboardingDbContext db, Market market, List<PendingUpload> uploads, CancellationToken cancellationToken)
    {
        if (uploads.Count == 0)
        {
            return null;
        }

        IResult? problem = null;
        foreach (var upload in uploads)
        {
            try
            {
                await documents.UploadAsync(market.Code, upload.Document.BlobName, upload.Bytes, cancellationToken);
                upload.Document.State = DocumentState.Stored;
            }
            catch (TransientFailureException ex)
            {
                logger.LogWarning("Uploading document {DocumentId} in {Market} failed: {Reason}", upload.Document.Id, market.Code, ex.Reason);
                problem = StorageUnavailable();
                break;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var upload in uploads.Where(u => u.Document.State == DocumentState.Stored))
            {
                await documents.DeleteAsync(market.Code, upload.Document.BlobName, CancellationToken.None);
            }

            return Changed();
        }

        return problem;
    }

    private static List<PendingUpload> AddDocuments(OnboardingDbContext db, Application application, List<DecodedImage> images, DateTimeOffset now)
    {
        var uploads = new List<PendingUpload>();
        foreach (var image in images)
        {
            var version = application.Documents.Where(d => d.Type == image.Type).Select(d => d.Version).DefaultIfEmpty(0).Max() + 1;
            var document = new Document
            {
                ApplicationId = application.Id,
                Type = image.Type,
                Version = version,
                SizeBytes = image.Bytes.Length,
                Sha256 = image.Sha256,
                State = DocumentState.Pending,
                CreatedAt = now,
            };
            db.Documents.Add(document);
            application.Documents.Add(document);
            document.BlobName = $"{application.Id}/{document.Id}";
            uploads.Add(new PendingUpload(document, image.Bytes));
        }

        return uploads;
    }

    private async Task<IResult> WaitAndRespondAsync(Market market, Application application, CancellationToken cancellationToken)
    {
        var current = await waiter.WaitAsync(market.Code, application.Id, cancellationToken) ?? application;
        var status = ApplicationViews.Status(current);
        return CustomerStatus.IsFinal(status.Status)
            ? TypedResults.Ok(status)
            : TypedResults.Accepted(Location(market, application.Id), status);
    }

    private string? Identifier(Market market, Application application) =>
        application.Applicant.IdentifierValue is { } encrypted ? cipher.DecryptString(market.Code, encrypted) : null;

    private Market Market(string code) =>
        markets.TryGet(code, out var market) ? market : throw new InvalidOperationException("The market filter let an unknown market through.");

    private static Task<Application?> LoadAsync(OnboardingDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.Include(a => a.Applicant).Include(a => a.Documents).SingleOrDefaultAsync(a => a.Id == applicationId, cancellationToken);

    private static Task<Application?> LoadByKeyAsync(OnboardingDbContext db, string key, CancellationToken cancellationToken) =>
        db.Applications.Include(a => a.Applicant).Include(a => a.Documents).SingleOrDefaultAsync(a => a.IdempotencyKey == key, cancellationToken);

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    private static string Location(Market market, Guid applicationId) => $"/v1/markets/{market.Code}/applications/{applicationId}";

    private static IResult NotFound() =>
        Problems.Of(StatusCodes.Status404NotFound, "application.notFound", "No such application in this market.");

    private static IResult Withdrawn() =>
        Problems.Of(StatusCodes.Status409Conflict, "application.withdrawn", "The application was withdrawn on an erasure request.");

    private static IResult Changed() =>
        Problems.Of(StatusCodes.Status409Conflict, "application.changed", "The application changed at the same moment. Try again.");

    private static IResult StorageUnavailable() =>
        Problems.Of(StatusCodes.Status503ServiceUnavailable, "documents.unavailable", "Document storage is unavailable. Send the same request again.");
}
