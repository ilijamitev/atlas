namespace Atlas.Onboarding;

public sealed class OnboardingOptions
{
    /// <summary>
    /// How long a submit (or a complete create) waits for a decision before answering with the current status. It
    /// stays under OkHttp's default 10-second read timeout, so an Android client on default settings doesn't give up
    /// first.
    /// </summary>
    public TimeSpan DecisionWait { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The largest request body accepted: two images of several MB each, as base64.</summary>
    public long MaxRequestBytes { get; set; } = 24 * 1024 * 1024;

    /// <summary>The largest single image, once decoded.</summary>
    public int MaxImageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Creates allowed per client IP per window. Create is the one anonymous endpoint.</summary>
    public int CreatePermitLimit { get; set; } = 20;

    public TimeSpan CreateWindow { get; set; } = TimeSpan.FromMinutes(1);

    public WorkerOptions Worker { get; set; } = new();
}

public sealed class WorkerOptions
{
    /// <summary>How often an idle worker looks for due work. A submit wakes it at once.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int BatchSize { get; set; } = 16;

    /// <summary>Applications one instance processes at the same time.</summary>
    public int MaxParallel { get; set; } = 8;

    /// <summary>How long a claim keeps other replicas away. It outlasts the longest step, timeouts included.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Backoff after a technical failure: doubles from the base, with jitter, up to the maximum.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Checks on a referred application: after a minute, then doubling up to every 15 minutes.</summary>
    public TimeSpan ReviewFirstCheck { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan ReviewMaxInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often the worker removes document rows whose upload never finished.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A document still <c>Pending</c> after this long is an abandoned upload.</summary>
    public TimeSpan PendingDocumentTimeout { get; set; } = TimeSpan.FromHours(1);
}
