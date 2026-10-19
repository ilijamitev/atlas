using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Atlas.Shared.Markets;

/// <summary>Fails startup on a broken market config, instead of failing applicants later.</summary>
internal sealed partial class MarketsOptionsValidator : IValidateOptions<MarketsOptions>
{
    public ValidateOptionsResult Validate(string? name, MarketsOptions options)
    {
        var failures = new List<string>();
        if (options.Markets.Count == 0)
        {
            failures.Add("Atlas:Markets is empty.");
        }

        foreach (var (code, market) in options.Markets)
        {
            if (!TwoLetters().IsMatch(code))
            {
                failures.Add($"Market code '{code}' must be two letters.");
            }

            if (!TwoLetters().IsMatch(market.Country) || market.Country != market.Country.ToUpperInvariant())
            {
                failures.Add($"{code}: Country must be an ISO 3166 alpha-2 code in capitals.");
            }

            if (market.ConfidenceThreshold is <= 0 or > 1)
            {
                failures.Add($"{code}: ConfidenceThreshold must be above 0 and at most 1.");
            }

            if (market.MaxRetakes < 0)
            {
                failures.Add($"{code}: MaxRetakes can't be negative.");
            }

            if (market.MinimumAge is < 0 or > 120)
            {
                failures.Add($"{code}: MinimumAge must be between 0 and 120.");
            }

            if (market.Identifiers.Count == 0)
            {
                failures.Add($"{code}: at least one identifier type is required.");
            }

            foreach (var (type, rule) in market.Identifiers)
            {
                if (!IdentifierTypes.All.Contains(type))
                {
                    failures.Add($"{code}: unknown identifier type '{type}'.");
                }

                if (!IsValidPattern(rule.Pattern))
                {
                    failures.Add($"{code}.{type}: Pattern is missing or not a valid regular expression.");
                }
            }
        }

        foreach (var code in options.EnabledCodes().Where(code => !options.Markets.Keys.Any(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase))))
        {
            failures.Add($"EnabledMarkets lists '{code}', which isn't configured.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex TwoLetters();
}
