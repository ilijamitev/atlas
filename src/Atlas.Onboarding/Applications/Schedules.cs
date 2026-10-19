namespace Atlas.Onboarding.Applications;

/// <summary>When to ask Screening again about a referred application: the first wait, then doubling, capped.</summary>
public sealed record ReviewSchedule(TimeSpan First, TimeSpan Max)
{
    /// <summary>The wait before check <paramref name="check"/>, counting from 0.</summary>
    public TimeSpan Interval(int check) => Doubled(First, check, Max);

    internal static TimeSpan Doubled(TimeSpan start, int times, TimeSpan max)
    {
        var ticks = start.Ticks * Math.Pow(2, Math.Clamp(times, 0, 30));
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }
}

/// <summary>
/// The wait after a technical failure: doubling from the base, capped, with ±20% jitter so applications parked by the
/// same outage don't all come back at once.
/// </summary>
public sealed record RetryBackoff(TimeSpan Base, TimeSpan Max)
{
    /// <summary>The wait after failure number <paramref name="failure"/>, counting from 1.</summary>
    public TimeSpan Delay(int failure, double jitter)
    {
        var delay = ReviewSchedule.Doubled(Base, failure - 1, Max);
        return delay * (0.8 + 0.4 * Math.Clamp(jitter, 0, 1));
    }
}
