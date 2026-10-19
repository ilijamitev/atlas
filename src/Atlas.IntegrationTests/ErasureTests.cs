using System.Net;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Persistence;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atlas.IntegrationTests;

/// <summary>GC #5 against GC #4: drafts are deleted on request, submitted applications are restricted until retention ends.</summary>
[Collection(AtlasCollection.Name)]
public sealed class ErasureTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Erasing_a_draft_deletes_its_rows_and_images_and_logs_the_erasure()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Only(TestData.Complete("MB"), "firstName", "lastName", "documents"));
        var blobNames = await BlobNamesAsync(created.ApplicationId);
        Assert.Equal(2, blobNames.Count);

        var response = await app.EraseAsync("MB", created.ApplicationId, created.ApplicationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("DELETED", (await response.ReadAsync<ErasureResponse>()).Outcome);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MB", created.ApplicationId, created.ApplicationToken)).StatusCode);

        await using var db = Database();
        Assert.False(await db.Applications.AnyAsync(a => a.Id == created.ApplicationId));
        Assert.False(await db.Applicants.AnyAsync(a => a.ApplicationId == created.ApplicationId));
        Assert.False(await db.Documents.AnyAsync(d => d.ApplicationId == created.ApplicationId));
        Assert.Contains(await db.AccessLog.Where(e => e.ResourceId == created.ApplicationId.ToString()).Select(e => e.Action).ToListAsync(), action => action == "erase");

        var container = Container("MB");
        foreach (var name in blobNames)
        {
            Assert.False((await container.GetBlobClient(name).ExistsAsync()).Value);
        }
    }

    [Fact]
    public async Task Erasing_a_submitted_application_restricts_it_until_retention_ends()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        var response = await app.EraseAsync("MB", created.ApplicationId, created.ApplicationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var erasure = await response.ReadAsync<ErasureResponse>();
        Assert.Equal("RESTRICTED", erasure.Outcome);
        Assert.InRange(erasure.DeleteAfter!.Value, DateTimeOffset.UtcNow.AddYears(10).AddDays(-2), DateTimeOffset.UtcNow.AddYears(10).AddDays(2));

        var details = await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken);
        Assert.Equal(CustomerStatus.Withdrawn, details.Status);
        Assert.Equal(NextAction.None, details.NextAction);

        var patch = await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { email = "other@example.test" });
        Assert.Equal("application.withdrawn", await patch.ProblemCodeAsync());

        // The record and its images stay for retention (GC #4); only processing stops.
        Assert.Equal(2, (await BlobNamesAsync(created.ApplicationId)).Count);
    }

    private OnboardingDbContext Database() =>
        atlas.Onboarding.Services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>().Create("MB");

    private async Task<List<string>> BlobNamesAsync(Guid applicationId)
    {
        await using var db = Database();
        return await db.Documents.Where(d => d.ApplicationId == applicationId).Select(d => d.BlobName).ToListAsync();
    }

    private BlobContainerClient Container(string market) =>
        atlas.Onboarding.Services.GetRequiredService<BlobServiceClient>()
            .GetBlobContainerClient(atlas.Onboarding.Services.GetRequiredService<IOptions<BlobOptions>>().Value.ContainerName(market));
}
