using Microsoft.Extensions.Options;

namespace Atlas.Simulators.Control;

public enum FaultMode
{
    /// <summary>Answers normally, after the provider's usual latency.</summary>
    Healthy,

    /// <summary>Every call gets 503 at once.</summary>
    Down,

    /// <summary>Every call gets 500 at once.</summary>
    Error500,

    /// <summary>Answers normally, but only after 9 seconds: longer than the 8-second wait, shorter than a client timeout.</summary>
    Slow,

    /// <summary>Never answers, so the caller's timeout fires.</summary>
    Timeout,

    /// <summary>Every call gets 422 at once: a permanent refusal, such as core banking declining to open an account.</summary>
    Reject,
}

/// <summary>
/// A provider's fault settings. <c>FailNext</c> calls get 503 before <c>Mode</c> applies again. <c>LatencyMs</c>
/// replaces the provider's usual latency for answers.
/// </summary>
public sealed record FaultSettings(FaultMode Mode, int FailNext, int? LatencyMs)
{
    public static readonly FaultSettings Healthy = new(FaultMode.Healthy, 0, null);
}

internal enum FaultKind
{
    None,
    Unavailable,
    ServerError,
    Hang,
    Refuse,
}

internal readonly record struct FaultDecision(FaultKind Kind, TimeSpan Delay);

/// <summary>What each stand-in does on its next call. Reviewers and tests change it over HTTP, never by editing code.</summary>
internal sealed class FaultControl(IOptions<SimulatorOptions> options)
{
    private static readonly TimeSpan SlowDelay = TimeSpan.FromSeconds(9);

    private readonly Lock _lock = new();
    private readonly Dictionary<Provider, FaultSettings> _settings = [];

    public FaultSettings Get(Provider provider)
    {
        lock (_lock)
        {
            return _settings.GetValueOrDefault(provider, FaultSettings.Healthy);
        }
    }

    public void Set(Provider provider, FaultSettings settings)
    {
        lock (_lock)
        {
            _settings[provider] = settings;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _settings.Clear();
        }
    }

    /// <summary>Decides the next call's fate, counting down <c>FailNext</c>.</summary>
    public FaultDecision Next(Provider provider)
    {
        lock (_lock)
        {
            var settings = _settings.GetValueOrDefault(provider, FaultSettings.Healthy);
            var latency = settings.LatencyMs is { } ms ? TimeSpan.FromMilliseconds(ms) : options.Value.Latency(provider);

            if (settings.FailNext > 0)
            {
                _settings[provider] = settings with { FailNext = settings.FailNext - 1 };
                return new FaultDecision(FaultKind.Unavailable, latency);
            }

            return settings.Mode switch
            {
                FaultMode.Down => new FaultDecision(FaultKind.Unavailable, latency),
                FaultMode.Error500 => new FaultDecision(FaultKind.ServerError, latency),
                FaultMode.Slow => new FaultDecision(FaultKind.None, settings.LatencyMs is null ? SlowDelay : latency),
                FaultMode.Timeout => new FaultDecision(FaultKind.Hang, latency),
                FaultMode.Reject => new FaultDecision(FaultKind.Refuse, latency),
                _ => new FaultDecision(FaultKind.None, latency),
            };
        }
    }
}
