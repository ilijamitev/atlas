using Atlas.Onboarding.Applications;
using Atlas.Shared.Http;

namespace Atlas.Onboarding.Clients;

/// <summary>
/// Core banking, which the ticket leaves unspecified. Both calls are keyed by application ID, so retrying the account
/// step never opens a second account or orders a second card (AC6).
/// </summary>
internal sealed class CoreBankingClient(IHttpClientFactory clients)
{
    public const string Name = "corebanking";

    private sealed record Holder(string FirstName, string LastName, string DateOfBirth);

    private sealed record AccountRequest(Guid ApplicationId, string Market, Holder Holder);

    private sealed record AccountResponse(string AccountId);

    private sealed record CardRequest(Guid ApplicationId, string AccountId);

    private sealed record CardResponse(string CardId, string Status);

    public async Task<string> OpenAccountAsync(string market, Guid applicationId, Applicant applicant, CancellationToken cancellationToken)
    {
        var holder = new Holder(applicant.FirstName!, applicant.LastName!, applicant.DateOfBirth!.Value.ToString("yyyy-MM-dd"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/accounts")
        {
            Content = JsonContent.Create(new AccountRequest(applicationId, market.ToUpperInvariant(), holder)),
        };
        request.Headers.Add("Idempotency-Key", $"{applicationId}:account");

        using var response = await clients.CreateClient(Name).SendClassifiedAsync(request, Name, cancellationToken);
        return (await response.ReadAnswerAsync<AccountResponse>(Name, cancellationToken)).AccountId;
    }

    public async Task<string> OrderCardAsync(Guid applicationId, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/cards")
        {
            Content = JsonContent.Create(new CardRequest(applicationId, accountId)),
        };
        request.Headers.Add("Idempotency-Key", $"{applicationId}:card");

        using var response = await clients.CreateClient(Name).SendClassifiedAsync(request, Name, cancellationToken);
        return (await response.ReadAnswerAsync<CardResponse>(Name, cancellationToken)).CardId;
    }
}
