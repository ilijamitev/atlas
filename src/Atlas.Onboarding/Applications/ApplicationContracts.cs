namespace Atlas.Onboarding.Applications;

/// <summary>The identifier, split three ways (Annex B): many MF residents only have a passport.</summary>
public sealed record IdentifierInput(string? Type, string? Value, string? IssuingCountry);

public sealed record DocumentInput(string? Type, string? Image);

/// <summary>
/// The ticket's body, apart from the identifier and country fields. Create and PATCH both take it, and every field is
/// optional until submit. A create with every field submits at once.
/// </summary>
public sealed record ApplicationInput(
    string? FirstName,
    string? LastName,
    string? DateOfBirth,
    string? Nationality,
    string? CountryOfResidence,
    IdentifierInput? Identifier,
    string? Email,
    string? Phone,
    List<DocumentInput>? Documents,
    bool? TermsAccepted)
{
    /// <summary>Anything besides photos: after a retake request, only new photos are accepted.</summary>
    public bool ChangesDetails =>
        FirstName is not null || LastName is not null || DateOfBirth is not null || Nationality is not null
        || CountryOfResidence is not null || Identifier is not null || Email is not null || Phone is not null
        || TermsAccepted is not null;
}

/// <summary>The status every call returns: a neutral status, what the app should do next, and a message code to translate.</summary>
public sealed record ApplicationStatusResponse(
    Guid ApplicationId,
    string Status,
    string NextAction,
    string MessageCode,
    IReadOnlyList<string> MissingFields);

/// <summary>The token is returned once, here. Only its hash is stored.</summary>
public sealed record ApplicationCreatedResponse(
    Guid ApplicationId,
    string ApplicationToken,
    string Status,
    string NextAction,
    string MessageCode,
    IReadOnlyList<string> MissingFields);

public sealed record IdentifierView(string Type, string Value, string? IssuingCountry);

public sealed record ApplicantView(
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    string? Nationality,
    string? CountryOfResidence,
    IdentifierView? Identifier,
    string? Email,
    string? Phone,
    bool TermsAccepted);

public sealed record DocumentView(string Type, int Version, string State, DateTimeOffset UploadedAt);

/// <summary>What the app needs to resume: the details so far and which photos arrived. Never the images.</summary>
public sealed record ApplicationDetailsResponse(
    Guid ApplicationId,
    string Status,
    string NextAction,
    string MessageCode,
    IReadOnlyList<string> MissingFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt,
    ApplicantView Applicant,
    IReadOnlyList<DocumentView> Documents);

public sealed record ErasureResponse(Guid ApplicationId, string Outcome, DateTimeOffset? DeleteAfter);

/// <summary>Both confirmations are required before an MD account is activated (Annex B).</summary>
public sealed record BranchActivationRequest(bool IdentityDocumentChecked, bool WetSignatureCollected);
