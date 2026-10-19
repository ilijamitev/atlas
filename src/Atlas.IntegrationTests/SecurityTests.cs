using System.Net;
using System.Text;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Documents;
using Atlas.Onboarding.Persistence;
using Atlas.Shared.Auth;
using Atlas.Shared.Contracts;
using Atlas.Shared.Persistence;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atlas.IntegrationTests;

/// <summary>GC-2026-0814 #1 and #2: who gets in, what's logged, and what's readable at rest.</summary>
[Collection(AtlasCollection.Name)]
public sealed class SecurityTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_application_is_only_readable_with_its_own_token()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var mine = await app.CreateOkAsync("MB", new { firstName = TestData.FirstName });
        var theirs = await app.CreateOkAsync("MB", new { firstName = TestData.FirstName });

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MB", mine.ApplicationId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MB", mine.ApplicationId, "not-the-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MB", mine.ApplicationId, theirs.ApplicationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("MA", mine.ApplicationId, mine.ApplicationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("MB", mine.ApplicationId, mine.ApplicationToken)).StatusCode);
    }

    [Fact]
    public async Task Screenings_are_for_services_only()
    {
        var path = ScreeningRoutes.For("MB", Guid.NewGuid());
        var screening = atlas.Screening.CreateClient();

        var anonymous = await screening.GetAsync(path);
        var officer = await screening.SendAsync(OnboardingClient.Request(HttpMethod.Get, path, bearer: atlas.Token(Roles.Officer, "MB")));
        var service = await screening.SendAsync(OnboardingClient.Request(HttpMethod.Get, path, bearer: atlas.Token(Roles.Service, null)));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, officer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, service.StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_states_who_may_call_it()
    {
        // A new endpoint without RequireAuthorization or an explicit AllowAnonymous fails here.
        string[] open = ["/health/live", "/health/ready", "/openapi/{documentName}.json"];
        foreach (var services in new[] { atlas.Onboarding.Services, atlas.Screening.Services })
        {
            var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => !open.Contains(e.RoutePattern.RawText));
            Assert.All(endpoints, endpoint => Assert.True(
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                $"{endpoint.DisplayName} declares no authorization."));
        }
    }

    [Fact]
    public async Task Images_are_encrypted_in_blob_storage_under_names_with_no_personal_data()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));
        await using var db = Database("MB");
        var documents = await db.Documents.Where(d => d.ApplicationId == created.ApplicationId).ToListAsync();
        var container = atlas.Onboarding.Services.GetRequiredService<BlobServiceClient>()
            .GetBlobContainerClient(atlas.Onboarding.Services.GetRequiredService<IOptions<BlobOptions>>().Value.ContainerName("MB"));

        Assert.Equal(2, documents.Count);
        foreach (var document in documents)
        {
            Assert.Matches("^[0-9a-f-]{36}/[0-9a-f-]{36}$", document.BlobName);
            var stored = (await container.GetBlobClient(document.BlobName).DownloadContentAsync()).Value.Content.ToArray();
            Assert.False(stored.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]), "The blob is a readable JPEG.");
            Assert.DoesNotContain("ATLAS TEST IMAGE", Encoding.ASCII.GetString(stored));
        }
    }

    [Fact]
    public async Task The_identifier_is_ciphertext_in_the_database()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        await using var db = Database("MB");
        var stored = await db.Applicants.Where(a => a.ApplicationId == created.ApplicationId).Select(a => a.IdentifierValue).SingleAsync();

        Assert.NotNull(stored);
        Assert.DoesNotContain(TestData.Markets["MB"].Identifier, Encoding.UTF8.GetString(stored));
    }

    [Fact]
    public async Task Every_read_and_change_by_the_applicant_is_in_the_access_log()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", new { firstName = TestData.FirstName });
        await app.PatchAsync("MB", created.ApplicationId, created.ApplicationToken, new { lastName = "Tester" });
        await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken);
        await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken);

        await using var db = Database("MB");
        var entries = await db.AccessLog.Where(e => e.ResourceId == created.ApplicationId.ToString()).OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(["create", "amend", "read", "read"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(("applicant", created.ApplicationId.ToString()), (e.ActorType, e.ActorId)));
    }

    [Fact]
    public async Task The_worker_logs_its_own_reads_under_the_service_name()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB"));

        await using var db = Database("MB");
        var workerReads = await db.AccessLog
            .Where(e => e.ResourceId == created.ApplicationId.ToString() && e.ActorType == Roles.Service)
            .Select(e => new { e.ActorId, e.Action })
            .ToListAsync();

        // IDNow, screening, and the account: three reads, each attributable to the onboarding service.
        Assert.Equal(3, workerReads.Count);
        Assert.All(workerReads, read => Assert.Equal(("onboarding", "read"), (read.ActorId, read.Action)));
    }

    private OnboardingDbContext Database(string market) =>
        atlas.Onboarding.Services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>().Create(market);
}
