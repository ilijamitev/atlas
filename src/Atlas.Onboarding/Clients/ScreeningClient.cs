using Atlas.Shared.Contracts;
using Atlas.Shared.Http;

namespace Atlas.Onboarding.Clients;

/// <summary>
/// The Screening service. Onboarding asks; Screening never calls back. Both calls are safe to repeat: the PUT is
/// idempotent per application, and the GET only reads the status.
/// </summary>
internal sealed class ScreeningClient(IHttpClientFactory clients)
{
    public const string Name = "screening";

    public async Task<string> ScreenAsync(string market, Guid applicationId, ScreeningRequest subject, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ScreeningRoutes.For(market, applicationId))
        {
            Content = JsonContent.Create(subject),
        };
        return await SendAsync(request, cancellationToken);
    }

    public async Task<string> StatusAsync(string market, Guid applicationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ScreeningRoutes.For(market, applicationId));
        return await SendAsync(request, cancellationToken);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient(Name).SendClassifiedAsync(request, Name, cancellationToken);
        var answer = await response.ReadAnswerAsync<ScreeningResponse>(Name, cancellationToken);
        return answer.Status is ScreeningStatus.Cleared or ScreeningStatus.Referred or ScreeningStatus.Rejected
            ? answer.Status
            : throw new PermanentFailureException(Name, "unknown screening status");
    }
}
