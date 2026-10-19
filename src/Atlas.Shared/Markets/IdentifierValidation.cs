using System.Text.RegularExpressions;

namespace Atlas.Shared.Markets;

public enum CheckDigitAlgorithm
{
    None,

    /// <summary>
    /// The ex-Yugoslav 13-digit personal number check, the likely candidate for MA and MB. Implemented but off in
    /// config: it rejects the ticket's own example 0403991450016 (check digit 6, the algorithm gives 4).
    /// </summary>
    Jmbg,
}

public static class IdentifierValidation
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Returns an error code (<c>identifier.format</c> or <c>identifier.checkDigit</c>), or null if valid.</summary>
    public static string? Validate(IdentifierRule rule, string value)
    {
        if (!Regex.IsMatch(value, rule.Pattern, RegexOptions.CultureInvariant, PatternTimeout))
        {
            return "identifier.format";
        }

        var checkDigitValid = rule.CheckDigit switch
        {
            CheckDigitAlgorithm.Jmbg => Jmbg.IsValid(value),
            _ => true,
        };
        return checkDigitValid ? null : "identifier.checkDigit";
    }
}

public static class Jmbg
{
    private static readonly int[] Weights = [7, 6, 5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    public static bool IsValid(string value)
    {
        if (value.Length != 13 || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (value[i] - '0') * Weights[i];
        }

        var remainder = 11 - (sum % 11);
        var expected = remainder > 9 ? 0 : remainder;
        return expected == value[12] - '0';
    }
}
