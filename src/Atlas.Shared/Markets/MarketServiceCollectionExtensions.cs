using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Markets;

public static class MarketServiceCollectionExtensions
{
    public static IServiceCollection AddAtlasMarkets(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MarketsOptions>().Bind(configuration.GetSection("Atlas")).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MarketsOptions>, MarketsOptionsValidator>();
        services.AddSingleton<IMarketCatalog, MarketCatalog>();
        return services;
    }
}
