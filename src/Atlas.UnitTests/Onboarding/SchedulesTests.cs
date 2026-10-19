using Atlas.Onboarding.Applications;

namespace Atlas.UnitTests.Onboarding;

public sealed class SchedulesTests
{
    [Fact]
    public void Retries_double_from_the_base_and_stop_at_the_maximum()
    {
        var backoff = new RetryBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

        // Jitter 0.5 is the midpoint: exactly the doubled delay.
        Assert.Equal([5, 10, 20, 40, 80, 160, 300, 300], Enumerable.Range(1, 8).Select(n => backoff.Delay(n, 0.5).TotalSeconds));
    }

    [Theory]
    [InlineData(0.0, 4)]
    [InlineData(1.0, 6)]
    public void Jitter_spreads_retries_by_a_fifth_either_way(double jitter, double seconds)
    {
        var backoff = new RetryBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

        Assert.Equal(seconds, backoff.Delay(1, jitter).TotalSeconds, precision: 6);
    }

    [Fact]
    public void A_huge_failure_count_does_not_overflow()
    {
        var backoff = new RetryBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), backoff.Delay(int.MaxValue, 0.5));
    }
}
