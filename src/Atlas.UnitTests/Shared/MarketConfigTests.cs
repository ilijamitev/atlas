using Atlas.Shared.Markets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Atlas.UnitTests.Shared;

/// <summary>The shipped atlas.json, and the rule that a market is configuration (AC4).</summary>
public sealed class MarketConfigTests
{
    [Fact]
    public void The_shipped_config_is_valid_and_has_all_six_markets()
    {
        var options = Bind(SharedSettings());

        Assert.True(new MarketsOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(["MA", "MB", "MC", "MD", "ME", "MF"], Catalog(options).All.Select(m => m.Code));
    }

    [Fact]
    public void Only_MD_activates_in_a_branch()
    {
        var catalog = Catalog(Bind(SharedSettings()));

        Assert.Equal(["MD"], catalog.All.Where(m => m.Options.Activation == ActivationMode.InBranch).Select(m => m.Code));
    }

    [Fact]
    public void Market_codes_are_never_used_as_country_codes()
    {
        // MA, MC, MD, ME and MF are real ISO codes for other places (Morocco, Monaco, Moldova, Montenegro, Saint Martin).
        var catalog = Catalog(Bind(SharedSettings()));

        Assert.All(catalog.All, m => Assert.NotEqual(m.Code, m.Options.Country));
    }

    [Fact]
    public void A_new_market_needs_only_config()
    {
        var settings = SharedSettings();
        settings.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Atlas:Markets:MG:Country"] = "XG",
            ["Atlas:Markets:MG:Activation"] = "Remote",
            ["Atlas:Markets:MG:Identifiers:NATIONAL_ID:Pattern"] = "^[0-9]{11}$",
            ["Atlas:Markets:MG:Identifiers:NATIONAL_ID:Description"] = "11 digits",
        });
        var options = Bind(settings);

        Assert.True(new MarketsOptionsValidator().Validate(null, options).Succeeded);
        Assert.True(Catalog(options).TryGet("mg", out var market));
        Assert.Null(IdentifierValidation.Validate(market.Options.Identifiers[IdentifierTypes.NationalId], "12345678901"));
    }

    [Fact]
    public void A_broken_market_stops_startup()
    {
        var options = new MarketsOptions
        {
            Markets =
            {
                ["MZ"] = new MarketOptions
                {
                    Country = "MZ1",
                    ConfidenceThreshold = 1.5,
                    Identifiers = { ["DRIVING_LICENCE"] = new IdentifierRule { Pattern = "([" } },
                },
            },
            EnabledMarkets = "MZ,MQ",
        };

        var result = new MarketsOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Country"));
        Assert.Contains(result.Failures!, f => f.Contains("ConfidenceThreshold"));
        Assert.Contains(result.Failures!, f => f.Contains("unknown identifier type"));
        Assert.Contains(result.Failures!, f => f.Contains("not a valid regular expression"));
        Assert.Contains(result.Failures!, f => f.Contains("'MQ'"));
    }

    [Fact]
    public void EnabledMarkets_narrows_what_a_deployment_serves()
    {
        var settings = SharedSettings();
        settings.AddInMemoryCollection(new Dictionary<string, string?> { ["Atlas:EnabledMarkets"] = "md, MB" });

        Assert.Equal(["MB", "MD"], Catalog(Bind(settings)).All.Select(m => m.Code));
    }

    internal static ConfigurationManager SharedSettings()
    {
        var configuration = new ConfigurationManager();
        configuration.SetBasePath(AppContext.BaseDirectory).AddJsonFile("atlas.json");
        return configuration;
    }

    internal static MarketsOptions Bind(IConfiguration configuration) =>
        configuration.GetSection("Atlas").Get<MarketsOptions>() ?? new MarketsOptions();

    internal static MarketCatalog Catalog(MarketsOptions options) => new(Options.Create(options));
}
