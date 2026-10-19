using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Atlas.Shared.Http;

/// <summary>
/// A call that may work later: a timeout, a network error, an open circuit, or a 408, 429 or 5xx after the in-call
/// retries. The caller parks the work and tries again; it never rejects anyone for it.
/// </summary>
public sealed class TransientFailureException(string target, string reason, Exception? inner = null)
    : Exception($"{target}: {reason}", inner)
{
    public string Target { get; } = target;
    public string Reason { get; } = reason;
}

/// <summary>
/// A call the other side refused for good: any other 4xx, or an answer that can't be read. Retrying won't help, so the
/// caller flags the work for operations instead.
/// </summary>
public sealed class PermanentFailureException(string target, string reason) : Exception($"{target}: {reason}")
{
    public string Target { get; } = target;
    public string Reason { get; } = reason;
}

public static class OutboundCalls
{
    /// <summary>Sends the request and sorts every failure into transient or permanent. Messages carry no personal data.</summary>
    public static async Task<HttpResponseMessage> SendClassifiedAsync(
        this HttpClient client, HttpRequestMessage request, string target, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new TransientFailureException(target, ex.GetType().Name, ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        response.Dispose();
        if (status is 408 or 429 or >= 500)
        {
            throw new TransientFailureException(target, $"HTTP {status}");
        }

        throw new PermanentFailureException(target, $"HTTP {status}");
    }

    /// <summary>Reads a JSON answer; one that can't be read is a permanent failure, not a crash.</summary>
    public static async Task<T> ReadAnswerAsync<T>(this HttpResponseMessage response, string target, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new PermanentFailureException(target, "empty answer");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new PermanentFailureException(target, "unreadable answer");
        }
    }
}
