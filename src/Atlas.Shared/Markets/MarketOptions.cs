namespace Atlas.Shared.Markets;

/// <summary>Everything that differs between markets (AC4: "Market differences are configuration").</summary>
public sealed class MarketOptions
{
    /// <summary>ISO 3166 alpha-2 code of the market's country. Never the internal market code.</summary>
    public string Country { get; set; } = "";

    public ActivationMode Activation { get; set; } = ActivationMode.Remote;

    /// <summary>Lowest IDNow confidence accepted without a retake.</summary>
    public double ConfidenceThreshold { get; set; } = 0.85;

    /// <summary>Retakes allowed after an inconclusive result, a face mismatch or low confidence.</summary>
    public int MaxRetakes { get; set; } = 2;

    public int MinimumAge { get; set; } = 18;

    /// <summary>Accepted identifier types (<see cref="IdentifierTypes"/>) and their rules (Annex B).</summary>
    public Dictionary<string, IdentifierRule> Identifiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum ActivationMode
{
    /// <summary>The account is opened as soon as screening clears.</summary>
    Remote,

    /// <summary>Annex B, MD: the applicant attends a branch, and staff confirm the ID check and wet signature.</summary>
    InBranch,
}

public sealed class IdentifierRule
{
    public string Pattern { get; set; } = "";
    public string Description { get; set; } = "";
    public CheckDigitAlgorithm CheckDigit { get; set; } = CheckDigitAlgorithm.None;
}

public static class IdentifierTypes
{
    public const string NationalId = "NATIONAL_ID";
    public const string Passport = "PASSPORT";

    public static readonly IReadOnlySet<string> All = new HashSet<string>([NationalId, Passport]);
}

/// <summary>The markets section of the shared settings.</summary>
public sealed class MarketsOptions
{
    public Dictionary<string, MarketOptions> Markets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Comma-separated market codes this deployment serves; empty means all. In production each country runs the
    /// same build with its own market here. Tests use it to create only the databases they need.
    /// </summary>
    public string? EnabledMarkets { get; set; }

    /// <summary>The market codes this deployment serves, in capitals.</summary>
    public IReadOnlyList<string> EnabledCodes() =>
        (string.IsNullOrWhiteSpace(EnabledMarkets)
            ? Markets.Keys.AsEnumerable()
            : EnabledMarkets.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Select(code => code.ToUpperInvariant())
        .Distinct()
        .Order()
        .ToList();
}
