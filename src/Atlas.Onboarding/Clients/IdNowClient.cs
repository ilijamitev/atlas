using Atlas.Onboarding.Applications;
using Atlas.Shared.Http;

namespace Atlas.Onboarding.Clients;

/// <summary>
/// IDNow, as the ticket describes it: the document and selfie images in, a document result and face match out. It
/// takes no personal details and returns no extracted data (see docs/open-questions.md).
/// </summary>
internal sealed class IdNowClient(IHttpClientFactory clients)
{
    public const string Name = "idnow";

    private sealed record IdentifyRequest(string DocumentType, string DocumentImage, string SelfieImage);

    private sealed record IdentifyResponse(string IdentificationId, string DocumentResult, bool FaceMatch, double Confidence);

    /// <param name="attempt">The retake number, so a retry of the same attempt reuses its idempotency key.</param>
    public async Task<IdentificationResult> IdentifyAsync(
        Guid applicationId, int attempt, string documentType, byte[] document, byte[] selfie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/identifications")
        {
            Content = JsonContent.Create(new IdentifyRequest(documentType, Convert.ToBase64String(document), Convert.ToBase64String(selfie))),
        };
        request.Headers.Add("Idempotency-Key", $"{applicationId}:idnow:{attempt}");

        using var response = await clients.CreateClient(Name).SendClassifiedAsync(request, Name, cancellationToken);
        var answer = await response.ReadAnswerAsync<IdentifyResponse>(Name, cancellationToken);
        if (answer.DocumentResult is not ("VALID" or "INVALID" or "INCONCLUSIVE"))
        {
            throw new PermanentFailureException(Name, "unknown document result");
        }

        return new IdentificationResult(answer.IdentificationId, answer.DocumentResult, answer.FaceMatch, answer.Confidence);
    }
}
