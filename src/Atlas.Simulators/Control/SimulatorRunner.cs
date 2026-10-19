namespace Atlas.Simulators.Control;

/// <summary>A stand-in's answer when no fault applies.</summary>
internal sealed record SimulatorAnswer(int Status, object Body, string Outcome)
{
    public static SimulatorAnswer BadRequest(string detail) =>
        new(StatusCodes.Status400BadRequest, new { error = "INVALID_REQUEST", detail }, $"rejected: {detail}");
}

/// <summary>
/// Runs one provider call: applies the fault settings and latency, then answers, and journals the outcome either
/// way.
/// </summary>
internal sealed class SimulatorRunner(FaultControl faults, RequestJournal journal, TimeProvider time)
{
    private static readonly TimeSpan MaxHang = TimeSpan.FromMinutes(2);

    public async Task<IResult> RunAsync(Provider provider, string operation, HttpContext http, Func<SimulatorAnswer> answer)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } header ? header : null;
        var fault = faults.Next(provider);

        // A provider that is down fails fast, as a real outage does; only answers and hangs take time.
        var delay = fault.Kind switch
        {
            FaultKind.None => fault.Delay,
            FaultKind.Hang => MaxHang,
            _ => TimeSpan.Zero,
        };

        try
        {
            await Task.Delay(delay, time, http.RequestAborted);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            journal.Record(provider, operation, key, 499, "caller gave up");
            return Results.Empty;
        }

        var result = fault.Kind switch
        {
            FaultKind.Unavailable => Fail(StatusCodes.Status503ServiceUnavailable, "SERVICE_UNAVAILABLE", "unavailable (simulated)"),
            FaultKind.ServerError => Fail(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "server error (simulated)"),
            FaultKind.Hang => Fail(StatusCodes.Status504GatewayTimeout, "TIMEOUT", "never answered (simulated)"),
            FaultKind.Refuse => Fail(StatusCodes.Status422UnprocessableEntity, "REFUSED", "refused (simulated)"),
            _ => answer(),
        };

        journal.Record(provider, operation, key, result.Status, result.Outcome);
        return Results.Json(result.Body, statusCode: result.Status);
    }

    private static SimulatorAnswer Fail(int status, string error, string outcome) => new(status, new { error }, outcome);
}
