using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Http;

/// <summary>
/// Timeouts and quick in-call retries for every outbound call. A call that still fails is the caller's to park and
/// retry later; nothing waits on a provider outage.
/// </summary>
public sealed class OutboundHttpOptions
{
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>At least 1. Retries back off exponentially with jitter from <see cref="RetryDelay"/>.</summary>
    public int Retries { get; set; } = 2;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

public static class OutboundHttpExtensions
{
    /// <summary>
    /// A named client whose base URL comes from <paramref name="baseUrlKey"/>, with a timeout per attempt, retries on
    /// 408, 429, 5xx and network errors, a circuit breaker and an overall timeout.
    /// </summary>
    public static IHttpClientBuilder AddOutboundClient(this IServiceCollection services, string name, string baseUrlKey, IConfiguration configuration)
    {
        services.AddOptions<OutboundHttpOptions>().Bind(configuration.GetSection("Atlas:Http"));

        var builder = services.AddHttpClient(name, (sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IConfiguration>()[baseUrlKey]
                ?? throw new InvalidOperationException($"{baseUrlKey} is not configured.");
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        });

        builder.AddStandardResilienceHandler().Configure((options, sp) =>
        {
            var http = sp.GetRequiredService<IOptions<OutboundHttpOptions>>().Value;
            options.AttemptTimeout.Timeout = http.AttemptTimeout;
            options.TotalRequestTimeout.Timeout = http.TotalTimeout;
            options.Retry.MaxRetryAttempts = Math.Max(1, http.Retries);
            options.Retry.Delay = http.RetryDelay;

            // The breaker's sampling window has to cover at least two attempts.
            if (options.CircuitBreaker.SamplingDuration < http.AttemptTimeout * 2)
            {
                options.CircuitBreaker.SamplingDuration = http.AttemptTimeout * 2;
            }
        });
        return builder;
    }
}
