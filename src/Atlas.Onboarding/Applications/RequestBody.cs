using System.Text.Json;
using Atlas.Shared.Http;

namespace Atlas.Onboarding.Applications;

/// <summary>
/// Reads an application body with a size limit. The raw bytes are kept because create hashes them, so a reused
/// Idempotency-Key with a different body can be refused.
/// </summary>
internal static class RequestBody
{
    public static async Task<(byte[]? Body, IResult? Error)> ReadAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return (null, TooLarge(limit));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (null, TooLarge(limit));
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), null);
    }

    public static (ApplicationInput? Input, IResult? Error) Parse(byte[] body)
    {
        try
        {
            var input = body.Length == 0 ? null : JsonSerializer.Deserialize<ApplicationInput>(body, JsonSerializerOptions.Web);
            return input is null ? (null, Malformed()) : (input, null);
        }
        catch (JsonException)
        {
            return (null, Malformed());
        }
    }

    private static IResult TooLarge(long limit) =>
        Problems.Of(StatusCodes.Status413PayloadTooLarge, "request.tooLarge", $"The body can be at most {limit / (1024 * 1024)} MB.");

    private static IResult Malformed() =>
        Problems.Of(StatusCodes.Status400BadRequest, "request.malformed", "The body must be a JSON application.");
}
