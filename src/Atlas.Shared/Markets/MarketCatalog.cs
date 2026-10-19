using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Markets;

public sealed record Market(string Code, MarketOptions Options);

/// <summary>The markets this deployment serves.</summary>
public interface IMarketCatalog
{
    IReadOnlyList<Market> All { get; }

    bool TryGet(string? code, [NotNullWhen(true)] out Market? market);
}

internal sealed class MarketCatalog : IMarketCatalog
{
    private readonly Dictionary<string, Market> _byCode;

    public MarketCatalog(IOptions<MarketsOptions> options)
    {
        var configured = options.Value.Markets;
        All = options.Value.EnabledCodes()
            .Select(code => new Market(code, configured.First(m => string.Equals(m.Key, code, StringComparison.OrdinalIgnoreCase)).Value))
            .ToList();
        _byCode = All.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Market> All { get; }

    public bool TryGet(string? code, [NotNullWhen(true)] out Market? market)
    {
        market = null;
        return code is not null && _byCode.TryGetValue(code, out market);
    }
}
