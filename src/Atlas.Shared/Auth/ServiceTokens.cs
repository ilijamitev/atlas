using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Shared.Auth;

/// <summary>The name a service acts under: in its calls to other services and in the access log.</summary>
public sealed record ServiceIdentity(string Name);

/// <summary>This service's own token for calls to other Atlas services, renewed well before it expires.</summary>
internal sealed class ServiceTokens(DevTokenIssuer issuer, ServiceIdentity identity, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private string? _token;
    private DateTimeOffset _renewAt;

    public string Current()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_token is null || now >= _renewAt)
            {
                _token = issuer.Issue(identity.Name, Roles.Service, lifetime: Lifetime);
                _renewAt = now + Lifetime - RenewBefore;
            }

            return _token;
        }
    }
}

internal sealed class ServiceTokenHandler(ServiceTokens tokens) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Current());
        return base.SendAsync(request, cancellationToken);
    }
}

public static class ServiceTokenExtensions
{
    /// <summary>Sends this service's token with every call the client makes.</summary>
    public static IHttpClientBuilder WithServiceToken(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler<ServiceTokenHandler>();
}
