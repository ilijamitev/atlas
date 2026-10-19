using Atlas.IntegrationTests.Support;
using Atlas.Onboarding;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Markets;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Atlas.IntegrationTests;

/// <summary>A crash between the row and the upload leaves a <c>Pending</c> row; the sweep removes it, and its blob, after an hour.</summary>
[Collection(AtlasCollection.Name)]
public sealed class DocumentSweepTests(AtlasFixture atlas)
{
    [Fact]
    public async Task An_abandoned_upload_is_removed_with_its_blob_after_an_hour_and_not_before()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", new { firstName = TestData.FirstName });
        var services = atlas.Onboarding.Services;
        var databases = services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>();
        var store = services.GetRequiredService<IDocumentStore>();

        // What a crash right after the upload leaves behind: the blob is there, the row still says Pending.
        var document = new Document
        {
            Id = Guid.NewGuid(),
            ApplicationId = created.ApplicationId,
            Type = DocumentTypes.Passport,
            Version = 1,
            SizeBytes = 4,
            Sha256 = new byte[32],
            State = DocumentState.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        document.BlobName = $"{created.ApplicationId}/{document.Id}";
        await using (var db = databases.Create("MB"))
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync();
        }

        await store.UploadAsync("MB", document.BlobName, [0xFF, 0xD8, 0xFF, 0xD9], CancellationToken.None);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(30));
        var sweeper = new DocumentSweeper(
            databases,
            services.GetRequiredService<IMarketCatalog>(),
            store,
            services.GetRequiredService<IOptions<OnboardingOptions>>(),
            clock,
            NullLogger<DocumentSweeper>.Instance);

        Assert.Equal(0, await sweeper.SweepAsync("MB", CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await sweeper.SweepAsync("MB", CancellationToken.None));

        await using var check = databases.Create("MB");
        Assert.False(await check.Documents.AnyAsync(d => d.Id == document.Id));
        await Assert.ThrowsAsync<Atlas.Shared.Http.PermanentFailureException>(() => store.DownloadAsync("MB", document.BlobName, CancellationToken.None));
    }
}
