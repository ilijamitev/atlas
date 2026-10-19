using System.Net;
using System.Net.Http.Json;
using Atlas.IntegrationTests.Support;
using Atlas.Onboarding.Applications;
using Atlas.Onboarding.Persistence;
using Atlas.Screening.Persistence;
using Atlas.Screening.Reviews;
using Atlas.Shared.AccessLog;
using Atlas.Shared.Auth;
using Atlas.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

/// <summary>GC-2026-0814 #3: a possible match waits for a compliance officer of the market, and only the officer decides.</summary>
[Collection(AtlasCollection.Name)]
public sealed class ReviewFlowTests(AtlasFixture atlas) : IAsyncLifetime
{
    public Task InitializeAsync() => atlas.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_possible_match_waits_for_an_officer_who_clears_it_and_the_account_is_opened()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester MATCH"));

        // The customer sees a neutral status, never the reason.
        Assert.Equal(CustomerStatus.UnderReview, created.Status);
        Assert.Equal(NextAction.Wait, created.NextAction);
        Assert.Equal("application.underReview", created.MessageCode);

        // Nothing decides it while the officer hasn't: Onboarding keeps asking and keeps waiting.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(CustomerStatus.UnderReview, (await app.GetOkAsync("MB", created.ApplicationId, created.ApplicationToken)).Status);
        Assert.Empty((await atlas.JournalAsync("corebanking", created.ApplicationId)));

        var officer = OfficerClient("MB");
        var reviewCase = await FindCaseAsync(officer, "MB", created.ApplicationId);
        Assert.Equal(["SANCTIONS"], reviewCase.ListTypes);
        Assert.False(reviewCase.Overdue);

        var detail = await officer.GetFromJsonAsync<CaseDetail>($"/v1/markets/MB/cases/{reviewCase.CaseId}");
        Assert.Equal("Tester MATCH", detail!.Subject.LastName);
        Assert.NotEmpty(detail.Matches);

        var decision = await officer.PostAsJsonAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}/decision", new { decision = "CLEAR", reason = "False positive: the list entry's date of birth differs." });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        await app.WaitForStatusAsync("MB", created.ApplicationId, created.ApplicationToken, CustomerStatus.Approved);
        Assert.Equal(["account", "card"], (await atlas.JournalAsync("corebanking", created.ApplicationId)).Select(e => e.Operation));
    }

    [Fact]
    public async Task An_officer_can_reject_and_the_customer_only_sees_a_neutral_rejection()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester PEP"));
        Assert.Equal(CustomerStatus.UnderReview, created.Status);

        var officer = OfficerClient("MB");
        var reviewCase = await FindCaseAsync(officer, "MB", created.ApplicationId);
        Assert.Equal(["PEP"], reviewCase.ListTypes);
        var decision = await officer.PostAsJsonAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}/decision", new { decision = "reject", reason = "Confirmed match; enhanced due diligence declined." });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        var rejected = await app.WaitForStatusAsync("MB", created.ApplicationId, created.ApplicationToken, CustomerStatus.Rejected);
        Assert.Equal("application.rejected", rejected.MessageCode);
        Assert.Empty(await atlas.JournalAsync("corebanking", created.ApplicationId));

        await using var db = atlas.Onboarding.Services.GetRequiredService<IMarketDbContextFactory<OnboardingDbContext>>().Create("MB");
        Assert.Equal(RejectionReasons.ScreeningRejected, (await db.Applications.SingleAsync(a => a.Id == created.ApplicationId)).RejectionReason);
    }

    [Fact]
    public async Task A_case_is_decided_once()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester MATCH"));
        var officer = OfficerClient("MB");
        var reviewCase = await FindCaseAsync(officer, "MB", created.ApplicationId);

        var first = await officer.PostAsJsonAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}/decision", new { decision = "CLEAR", reason = "Different person." });
        var second = await officer.PostAsJsonAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}/decision", new { decision = "REJECT", reason = "Changed my mind." });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("case.alreadyDecided", await second.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_decision_needs_a_reason_and_a_known_outcome()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester MATCH"));
        var officer = OfficerClient("MB");
        var reviewCase = await FindCaseAsync(officer, "MB", created.ApplicationId);

        var response = await officer.PostAsJsonAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}/decision", new { decision = "APPROVE" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["decision.unknown", "reason.required"], (await response.FieldCodesAsync()).Order());
    }

    [Fact]
    public async Task Only_an_officer_of_the_same_market_gets_in()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester MATCH"));
        var reviewCase = await FindCaseAsync(OfficerClient("MB"), "MB", created.ApplicationId);
        var path = $"/v1/markets/MB/cases/{reviewCase.CaseId}";
        var screening = atlas.Screening.CreateClient();

        var anonymous = await screening.GetAsync(path);
        var otherMarket = await screening.SendAsync(OnboardingClient.Request(HttpMethod.Get, path, bearer: atlas.Token(Roles.Officer, "MA")));
        var branchStaff = await screening.SendAsync(OnboardingClient.Request(HttpMethod.Get, path, bearer: atlas.Token(Roles.BranchStaff, "MB")));
        var service = await screening.SendAsync(OnboardingClient.Request(HttpMethod.Get, path, bearer: atlas.Token(Roles.Service, null)));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherMarket.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, branchStaff.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
    }

    [Fact]
    public async Task The_case_list_holds_no_personal_data_and_every_case_read_is_logged()
    {
        var app = new OnboardingClient(atlas.Onboarding.CreateClient());
        var created = await app.CreateOkAsync("MB", TestData.Complete("MB", lastName: "Tester MATCH"));
        var officer = OfficerClient("MB", subject: "officer-mb-7");

        var list = await officer.GetStringAsync("/v1/markets/MB/cases");
        Assert.DoesNotContain("Tester", list);
        Assert.DoesNotContain(TestData.DateOfBirth, list);

        var reviewCase = await FindCaseAsync(officer, "MB", created.ApplicationId);
        await officer.GetAsync($"/v1/markets/MB/cases/{reviewCase.CaseId}");

        await using var db = atlas.Screening.Services.GetRequiredService<IMarketDbContextFactory<ScreeningDbContext>>().Create("MB");
        var reads = await db.Set<AccessLogEntry>().Where(e => e.ResourceId == reviewCase.CaseId.ToString()).ToListAsync();
        var read = Assert.Single(reads);
        Assert.Equal(("officer", "officer-mb-7", "read", "case"), (read.ActorType, read.ActorId, read.Action, read.Resource));
    }

    private HttpClient OfficerClient(string market, string? subject = null)
    {
        var client = atlas.Screening.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", atlas.Token(Roles.Officer, market, subject));
        return client;
    }

    private static async Task<CaseSummary> FindCaseAsync(HttpClient officer, string market, Guid applicationId)
    {
        var cases = await officer.GetFromJsonAsync<List<CaseSummary>>($"/v1/markets/{market}/cases");
        return Assert.Single(cases!, c => c.ApplicationId == applicationId);
    }
}
