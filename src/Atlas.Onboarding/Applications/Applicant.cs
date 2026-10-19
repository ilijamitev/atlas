namespace Atlas.Onboarding.Applications;

/// <summary>
/// The applicant's personal data, one row per application, kept apart from the workflow columns so erasure and
/// restriction have one place to act on. The identifier value is encrypted with the market's key.
/// </summary>
public sealed class Applicant
{
    public Guid ApplicationId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>ISO 3166 alpha-2. World-Check screens against this, never against the market code.</summary>
    public string? Nationality { get; set; }

    /// <summary>ISO 3166 alpha-2; v1 serves residents of the market's country only.</summary>
    public string? CountryOfResidence { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>NATIONAL_ID or PASSPORT (Annex B). Never a key.</summary>
    public string? IdentifierType { get; set; }

    public byte[]? IdentifierValue { get; set; }
    public string? IdentifierIssuingCountry { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
}
