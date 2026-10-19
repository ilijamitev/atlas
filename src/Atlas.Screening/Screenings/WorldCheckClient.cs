using Atlas.Shared.Contracts;
using Atlas.Shared.Http;

namespace Atlas.Screening.Screenings;

/// <summary>
/// Refinitiv World-Check, as the ticket describes it. It gets the name as typed, the date of birth and the ISO
/// nationality: never a market code.
/// </summary>
internal sealed class WorldCheckClient(IHttpClientFactory clients)
{
    public const string Name = "worldcheck";

    private sealed record ScreenRequest(string Name, string DateOfBirth, string Nationality);

    private sealed record Match(string ListType, double Score, string Subject);

    private sealed record ScreenResponse(string CaseId, string Status, List<Match>? Matches);

    public async Task<WorldCheckResult> ScreenAsync(Guid applicationId, ScreeningRequest subject, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "worldcheck/v2/screen")
        {
            Content = JsonContent.Create(new ScreenRequest(
                $"{subject.FirstName} {subject.LastName}",
                subject.DateOfBirth.ToString("yyyy-MM-dd"),
                subject.Nationality)),
        };

        // World-Check shows no idempotency key; one is sent anyway, and the simulator journals it.
        request.Headers.Add("Idempotency-Key", $"{applicationId}:worldcheck");

        using var response = await clients.CreateClient(Name).SendClassifiedAsync(request, Name, cancellationToken);
        var answer = await response.ReadAnswerAsync<ScreenResponse>(Name, cancellationToken);
        var matches = (answer.Matches ?? []).Select(m => new WorldCheckMatchResult(m.ListType, m.Score, m.Subject)).ToList();

        return answer.Status switch
        {
            "CLEAR" => new WorldCheckResult(answer.CaseId, false, matches),
            "POSSIBLE_MATCH" => new WorldCheckResult(answer.CaseId, true, matches),
            _ => throw new PermanentFailureException(Name, "unknown status"),
        };
    }
}
