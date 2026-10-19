using System.Security.Cryptography;
using System.Text;
using Atlas.Onboarding.Applications;
using Atlas.Shared.Encryption;
using Atlas.Shared.Markets;
using Atlas.UnitTests.Shared;

namespace Atlas.UnitTests.Onboarding;

/// <summary>The field rules, run against the shipped atlas.json.</summary>
public sealed class ApplicationRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);
    private static readonly IMarketCatalog Markets = MarketConfigTests.Catalog(MarketConfigTests.Bind(MarketConfigTests.SharedSettings()));

    [Theory]
    [InlineData("MA", "NATIONAL_ID", "1234567890123", "XA")]
    [InlineData("MB", "NATIONAL_ID", "9876543210987", "XB")]
    [InlineData("MC", "NATIONAL_ID", "AB12CD34E", "XC")]
    [InlineData("MD", "NATIONAL_ID", "1234567890", "XD")]
    [InlineData("ME", "NATIONAL_ID", "1234567890", "XE")]
    [InlineData("MF", "NATIONAL_ID", "A1B2C3", "XF")]
    [InlineData("MF", "PASSPORT", "AB1234567", "XX")]
    public void Each_market_accepts_its_own_identifier(string market, string type, string value, string issuingCountry)
    {
        Assert.Empty(Validate(market, Input(identifier: new IdentifierInput(type, value, issuingCountry))));
    }

    [Theory]
    [InlineData("MB", "12345")]
    [InlineData("MB", "123456789012A")]
    [InlineData("MC", "AB12CD345")]
    [InlineData("MD", "12345678901")]
    public void A_wrong_format_is_identifier_format(string market, string value)
    {
        var country = Markets.All.Single(m => m.Code == market).Options.Country;

        var errors = Validate(market, Input(identifier: new IdentifierInput("NATIONAL_ID", value, country)));

        Assert.Equal(["identifier.format"], errors.Select(e => e.Code));
    }

    [Fact]
    public void Only_MF_accepts_a_passport_in_lieu()
    {
        var passport = new IdentifierInput("PASSPORT", "AB1234567", "XX");

        Assert.Equal(["identifier.typeNotAccepted"], Validate("MB", Input(identifier: passport)).Select(e => e.Code));
        Assert.Empty(Validate("MF", Input(identifier: passport)));
    }

    [Fact]
    public void A_national_ID_is_issued_by_the_markets_country()
    {
        var errors = Validate("MB", Input(identifier: new IdentifierInput("NATIONAL_ID", "1234567890123", "XA")));

        Assert.Equal(["identifier.issuingCountry.mismatch"], errors.Select(e => e.Code));
    }

    [Fact]
    public void Lower_case_input_is_normalised_before_it_is_checked()
    {
        Assert.Empty(Validate("MC", Input(identifier: new IdentifierInput("national_id", " ab12cd34e ", "xc"), nationality: "xc", residence: " xc")));
    }

    [Theory]
    [InlineData("2008-10-19", null)]
    [InlineData("2008-10-20", "dateOfBirth.tooYoung")]
    [InlineData("2027-01-01", "dateOfBirth.invalid")]
    [InlineData("1900-01-01", "dateOfBirth.invalid")]
    [InlineData("19/10/2008", "dateOfBirth.format")]
    public void The_minimum_age_is_counted_to_the_day(string dateOfBirth, string? code)
    {
        var errors = Validate("MB", Input(dateOfBirth: dateOfBirth));

        Assert.Equal(code is null ? Array.Empty<string>() : [code], errors.Select(e => e.Code));
    }

    [Fact]
    public void Residents_of_the_market_only()
    {
        Assert.Equal(["countryOfResidence.notServed"], Validate("MB", Input(residence: "XA")).Select(e => e.Code));
        Assert.Equal(["countryOfResidence.format"], Validate("MB", Input(residence: "MB1")).Select(e => e.Code));
    }

    [Theory]
    [InlineData("Test", true)]
    [InlineData("Test-Name O'Tester", true)]
    [InlineData("Tést", true)]
    [InlineData("Te$t", false)]
    [InlineData("", false)]
    public void Names_are_letters_and_a_few_separators(string name, bool valid)
    {
        Assert.Equal(valid, Validate("MB", Input(firstName: name)).Count == 0);
    }

    [Fact]
    public void Email_and_phone_have_formats()
    {
        var errors = Validate("MB", Input(email: "not-an-email", phone: "0700 000"));

        Assert.Equal(["email.format", "phone.format"], errors.Select(e => e.Code));
    }

    [Fact]
    public void Images_are_decoded_hashed_and_sniffed()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        var input = Input(documents:
        [
            new DocumentInput("passport", Convert.ToBase64String(jpeg)),
            new DocumentInput("SELFIE", "not base64!"),
            new DocumentInput("SELFIE", Convert.ToBase64String(Encoding.ASCII.GetBytes("plain text"))),
            new DocumentInput("DRIVING_LICENCE", Convert.ToBase64String(jpeg)),
        ]);

        var errors = ApplicationRules.Validate(input, Market("MB"), Today, 1024, out var images);

        Assert.Equal(
            [("documents[1].image", "documents.image.base64"), ("documents[2].image", "documents.image.format"), ("documents[3].type", "documents.type")],
            errors.Select(e => (e.Field, e.Code)));
        var image = Assert.Single(images);
        Assert.Equal("PASSPORT", image.Type);
        Assert.Equal(SHA256.HashData(jpeg), image.Sha256);
    }

    [Fact]
    public void An_image_over_the_limit_is_refused_before_decoding()
    {
        var input = Input(documents: [new DocumentInput("PASSPORT", Convert.ToBase64String(new byte[2048]))]);

        var errors = ApplicationRules.Validate(input, Market("MB"), Today, 1024, out _);

        Assert.Equal(["documents.image.tooLarge"], errors.Select(e => e.Code));
    }

    [Fact]
    public void An_empty_draft_is_missing_everything()
    {
        var application = Application.Start(new Applicant(), "key", [], [], DateTimeOffset.UnixEpoch);

        Assert.Equal(
            ["firstName", "lastName", "dateOfBirth", "nationality", "countryOfResidence", "identifier", "email", "phone", "documents.identity", "documents.selfie", "termsAccepted"],
            ApplicationRules.Missing(application));
    }

    [Fact]
    public void Apply_stores_the_identifier_encrypted_and_never_unaccepts_the_terms()
    {
        var cipher = FieldCipherTests.Cipher(("MB", "k1", [("k1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))]));
        var applicant = new Applicant();
        var now = DateTimeOffset.UnixEpoch;

        ApplicationRules.Apply(applicant, Input(identifier: new IdentifierInput("NATIONAL_ID", "1234567890123", "XB")) with { TermsAccepted = true }, "MB", cipher, now);
        ApplicationRules.Apply(applicant, Input() with { TermsAccepted = false }, "MB", cipher, now.AddDays(1));

        Assert.DoesNotContain("1234567890123", Encoding.UTF8.GetString(applicant.IdentifierValue!));
        Assert.Equal("1234567890123", cipher.DecryptString("MB", applicant.IdentifierValue!));
        Assert.Equal(now, applicant.TermsAcceptedAt);
    }

    private static List<Atlas.Shared.Http.FieldError> Validate(string market, ApplicationInput input) =>
        ApplicationRules.Validate(input, Market(market), Today, 1024 * 1024, out _);

    private static Market Market(string code) => Markets.All.Single(m => m.Code == code);

    private static ApplicationInput Input(
        string? firstName = null,
        string? dateOfBirth = null,
        string? nationality = null,
        string? residence = null,
        IdentifierInput? identifier = null,
        string? email = null,
        string? phone = null,
        List<DocumentInput>? documents = null) =>
        new(firstName, null, dateOfBirth, nationality, residence, identifier, email, phone, documents, null);
}
