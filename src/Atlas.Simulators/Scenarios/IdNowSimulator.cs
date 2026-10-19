using Atlas.Simulators.Control;

namespace Atlas.Simulators.Scenarios;

/// <summary>The ticket's IDNow request, trimmed to the fields Atlas uses.</summary>
public sealed record IdNowRequest(string? DocumentType, string? DocumentImage, string? SelfieImage);

public sealed record IdNowResponse(string IdentificationId, string DocumentResult, bool FaceMatch, double Confidence);

internal readonly record struct IdNowResult(string DocumentResult, bool FaceMatch, double Confidence);

/// <summary>
/// Stands in for IDNow. The image bytes choose the answer: a marker anywhere in the decoded image (the samples in
/// requests/samples carry one as plain text) picks a scenario, and anything else is a valid document with a matching
/// face.
/// </summary>
internal static class IdNowSimulator
{
    public static SimulatorAnswer Answer(IdNowRequest request)
    {
        if (request.DocumentType is not ("PASSPORT" or "ID_CARD"))
        {
            return SimulatorAnswer.BadRequest("documentType must be PASSPORT or ID_CARD.");
        }

        if (!TryDecode(request.DocumentImage, out var document) || !TryDecode(request.SelfieImage, out var selfie))
        {
            return SimulatorAnswer.BadRequest("documentImage and selfieImage must be non-empty base64.");
        }

        var result = Evaluate(document, selfie);
        var response = new IdNowResponse($"idn_{Guid.NewGuid():N}"[..16], result.DocumentResult, result.FaceMatch, result.Confidence);
        return new SimulatorAnswer(
            StatusCodes.Status200OK,
            response,
            $"{result.DocumentResult}, faceMatch {result.FaceMatch}, confidence {result.Confidence:0.00}");
    }

    public static IdNowResult Evaluate(ReadOnlySpan<byte> document, ReadOnlySpan<byte> selfie)
    {
        if (document.IndexOf("INVALID"u8) >= 0)
        {
            return new IdNowResult("INVALID", false, 0.30);
        }

        if (document.IndexOf("INCONCLUSIVE"u8) >= 0)
        {
            return new IdNowResult("INCONCLUSIVE", true, 0.55);
        }

        if (document.IndexOf("FACE_MISMATCH"u8) >= 0 || selfie.IndexOf("FACE_MISMATCH"u8) >= 0)
        {
            return new IdNowResult("VALID", false, 0.41);
        }

        if (document.IndexOf("LOW_CONFIDENCE"u8) >= 0 || selfie.IndexOf("LOW_CONFIDENCE"u8) >= 0)
        {
            return new IdNowResult("VALID", true, 0.62);
        }

        return new IdNowResult("VALID", true, 0.97);
    }

    private static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        var buffer = new byte[base64.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written == 0)
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
