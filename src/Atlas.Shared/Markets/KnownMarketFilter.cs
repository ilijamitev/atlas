using Atlas.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Shared.Markets;

/// <summary>A route's {market} must be one this deployment serves; anything else is 404, before any database is touched.</summary>
internal sealed class KnownMarketFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var markets = context.HttpContext.RequestServices.GetRequiredService<IMarketCatalog>();
        return markets.TryGet(context.HttpContext.GetRouteValue("market") as string, out _)
            ? next(context)
            : ValueTask.FromResult<object?>(Problems.Of(StatusCodes.Status404NotFound, "market.unknown", "This deployment doesn't serve that market."));
    }
}

public static class KnownMarketExtensions
{
    public static RouteGroupBuilder RequireKnownMarket(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<KnownMarketFilter>();
        return group;
    }
}
