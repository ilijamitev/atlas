using Atlas.Shared.Markets;

namespace Atlas.UnitTests.Shared;

/// <summary>Annex B's identifier formats, as configured in atlas.json.</summary>
public sealed class IdentifierRulesTests
{
    private static readonly IMarketCatalog Markets = MarketConfigTests.Catalog(MarketConfigTests.Bind(MarketConfigTests.SharedSettings()));

    [Theory]
    [InlineData("MA", "1234567890123", true)]
    [InlineData("MA", "123456789012", false)]
    [InlineData("MB", "1234567890123", true)]
    [InlineData("MB", "12345678901A3", false)]
    [InlineData("MC", "12345678A", true)]
    [InlineData("MC", "ABCDEFGHZ", true)]
    [InlineData("MC", "123456789", false)]
    [InlineData("MC", "1234567A", false)]
    [InlineData("MD", "1234567890", true)]
    [InlineData("MD", "123456789", false)]
    [InlineData("ME", "1234567890", true)]
    [InlineData("ME", "12345678901", false)]
    public void National_identifiers_follow_Annex_B(string market, string value, bool valid)
    {
        var rule = Rule(market, IdentifierTypes.NationalId);

        Assert.Equal(valid, IdentifierValidation.Validate(rule, value) is null);
    }

    [Fact]
    public void Only_MF_accepts_a_passport_in_lieu()
    {
        var withPassport = Markets.All.Where(m => m.Options.Identifiers.ContainsKey(IdentifierTypes.Passport)).Select(m => m.Code);

        Assert.Equal(["MF"], withPassport);
        Assert.Null(IdentifierValidation.Validate(Rule("MF", IdentifierTypes.Passport), "AB1234567"));
    }

    [Fact]
    public void Check_digits_are_off_so_the_tickets_own_example_is_accepted()
    {
        Assert.Null(IdentifierValidation.Validate(Rule("MB", IdentifierTypes.NationalId), "0403991450016"));
    }

    [Fact]
    public void A_JMBG_check_would_reject_the_tickets_own_example()
    {
        // The date part matches the ticket's date of birth (1991-03-04), but the check digit is 6; JMBG gives 4.
        Assert.False(Jmbg.IsValid("0403991450016"));
        Assert.True(Jmbg.IsValid("0403991450014"));

        var withJmbg = new IdentifierRule { Pattern = "^[0-9]{13}$", CheckDigit = CheckDigitAlgorithm.Jmbg };
        Assert.Equal("identifier.checkDigit", IdentifierValidation.Validate(withJmbg, "0403991450016"));
    }

    private static IdentifierRule Rule(string market, string type)
    {
        Assert.True(Markets.TryGet(market, out var found));
        return found.Options.Identifiers[type];
    }
}
