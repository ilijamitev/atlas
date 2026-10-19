using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Atlas.Shared.Encryption;
using Atlas.Shared.Http;
using Atlas.Shared.Markets;

namespace Atlas.Onboarding.Applications;

/// <summary>An uploaded image, decoded and hashed.</summary>
internal sealed record DecodedImage(string Type, byte[] Bytes, byte[] Sha256);

/// <summary>
/// Field rules, with the market's rules from config (Annex B). Every error has a stable code the app can translate,
/// and no message echoes what the applicant typed.
/// </summary>
internal static partial class ApplicationRules
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Checks the fields that are present. Missing fields are only an error at submit (<see cref="Missing"/>).</summary>
    public static List<FieldError> Validate(ApplicationInput input, Market market, DateOnly today, int maxImageBytes, out List<DecodedImage> images)
    {
        var errors = new List<FieldError>();
        images = [];

        Name("firstName", input.FirstName, errors);
        Name("lastName", input.LastName, errors);
        if (input.DateOfBirth is not null)
        {
            DateOfBirth(input.DateOfBirth, market.Options.MinimumAge, today, errors);
        }

        if (input.Nationality is not null && !IsCountry(Normalize(input.Nationality)))
        {
            errors.Add(new FieldError("nationality", "nationality.format", "Use an ISO 3166 alpha-2 country code."));
        }

        if (input.CountryOfResidence is not null)
        {
            var residence = Normalize(input.CountryOfResidence);
            if (!IsCountry(residence))
            {
                errors.Add(new FieldError("countryOfResidence", "countryOfResidence.format", "Use an ISO 3166 alpha-2 country code."));
            }
            else if (residence != market.Options.Country)
            {
                errors.Add(new FieldError("countryOfResidence", "countryOfResidence.notServed", $"This market serves residents of {market.Options.Country} only."));
            }
        }

        if (input.Identifier is not null)
        {
            Identifier(input.Identifier, market, errors);
        }

        if (input.Email is not null && (input.Email.Length > 254 || !EmailPattern().IsMatch(input.Email.Trim())))
        {
            errors.Add(new FieldError("email", "email.format", "Enter a valid email address."));
        }

        if (input.Phone is not null && !PhonePattern().IsMatch(input.Phone.Trim()))
        {
            errors.Add(new FieldError("phone", "phone.format", "Use the international format: + and 7 to 15 digits."));
        }

        if (input.Documents is not null)
        {
            Documents(input.Documents, maxImageBytes, errors, images);
        }

        return errors;
    }

    /// <summary>What still stands between a draft and submit, as field names the app can highlight.</summary>
    public static List<string> Missing(Application application)
    {
        var applicant = application.Applicant;
        var missing = new List<string>();
        if (applicant.FirstName is null)
        {
            missing.Add("firstName");
        }

        if (applicant.LastName is null)
        {
            missing.Add("lastName");
        }

        if (applicant.DateOfBirth is null)
        {
            missing.Add("dateOfBirth");
        }

        if (applicant.Nationality is null)
        {
            missing.Add("nationality");
        }

        if (applicant.CountryOfResidence is null)
        {
            missing.Add("countryOfResidence");
        }

        if (applicant.IdentifierType is null || applicant.IdentifierValue is null || applicant.IdentifierIssuingCountry is null)
        {
            missing.Add("identifier");
        }

        if (applicant.Email is null)
        {
            missing.Add("email");
        }

        if (applicant.Phone is null)
        {
            missing.Add("phone");
        }

        if (application.CurrentIdentityDocument is null)
        {
            missing.Add("documents.identity");
        }

        if (application.CurrentSelfie is null)
        {
            missing.Add("documents.selfie");
        }

        if (applicant.TermsAcceptedAt is null)
        {
            missing.Add("termsAccepted");
        }

        return missing;
    }

    /// <summary>Copies the fields that are present onto the applicant. Call only after <see cref="Validate"/> passed.</summary>
    public static void Apply(Applicant applicant, ApplicationInput input, string market, IFieldCipher cipher, DateTimeOffset now)
    {
        if (input.FirstName is not null)
        {
            applicant.FirstName = input.FirstName.Trim();
        }

        if (input.LastName is not null)
        {
            applicant.LastName = input.LastName.Trim();
        }

        if (input.DateOfBirth is not null)
        {
            applicant.DateOfBirth = DateOnly.ParseExact(input.DateOfBirth.Trim(), DateFormat, CultureInfo.InvariantCulture);
        }

        if (input.Nationality is not null)
        {
            applicant.Nationality = Normalize(input.Nationality);
        }

        if (input.CountryOfResidence is not null)
        {
            applicant.CountryOfResidence = Normalize(input.CountryOfResidence);
        }

        if (input.Identifier is { } identifier)
        {
            applicant.IdentifierType = Normalize(identifier.Type);
            applicant.IdentifierValue = cipher.EncryptString(market, Normalize(identifier.Value));
            applicant.IdentifierIssuingCountry = Normalize(identifier.IssuingCountry);
        }

        if (input.Email is not null)
        {
            applicant.Email = input.Email.Trim();
        }

        if (input.Phone is not null)
        {
            applicant.Phone = input.Phone.Trim();
        }

        if (input.TermsAccepted == true)
        {
            applicant.TermsAcceptedAt ??= now;
        }
    }

    private static void Name(string field, string? value, List<FieldError> errors)
    {
        if (value is not null && !NamePattern().IsMatch(value.Trim()))
        {
            errors.Add(new FieldError(field, $"{field}.format", "Use letters, spaces, apostrophes, hyphens and dots: 1 to 100 characters."));
        }
    }

    private static void DateOfBirth(string value, int minimumAge, DateOnly today, List<FieldError> errors)
    {
        if (!DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOfBirth))
        {
            errors.Add(new FieldError("dateOfBirth", "dateOfBirth.format", "Use the format yyyy-MM-dd."));
            return;
        }

        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age))
        {
            age--;
        }

        if (dateOfBirth > today || age > 120)
        {
            errors.Add(new FieldError("dateOfBirth", "dateOfBirth.invalid", "Enter a real date of birth."));
        }
        else if (age < minimumAge)
        {
            errors.Add(new FieldError("dateOfBirth", "dateOfBirth.tooYoung", $"Applicants must be at least {minimumAge}."));
        }
    }

    private static void Identifier(IdentifierInput identifier, Market market, List<FieldError> errors)
    {
        var type = Normalize(identifier.Type);
        if (!market.Options.Identifiers.TryGetValue(type, out var rule))
        {
            errors.Add(new FieldError(
                "identifier.type",
                "identifier.typeNotAccepted",
                $"{market.Code} accepts {string.Join(" or ", market.Options.Identifiers.Keys.Order())}."));
        }
        else if (Normalize(identifier.Value) is not { Length: > 0 } value)
        {
            errors.Add(new FieldError("identifier.value", "identifier.required", rule.Description));
        }
        else if (IdentifierValidation.Validate(rule, value) is { } code)
        {
            errors.Add(new FieldError("identifier.value", code, rule.Description));
        }

        var issuingCountry = Normalize(identifier.IssuingCountry);
        if (!IsCountry(issuingCountry))
        {
            errors.Add(new FieldError("identifier.issuingCountry", "identifier.issuingCountry.format", "Use an ISO 3166 alpha-2 country code."));
        }
        else if (type == IdentifierTypes.NationalId && issuingCountry != market.Options.Country)
        {
            errors.Add(new FieldError(
                "identifier.issuingCountry",
                "identifier.issuingCountry.mismatch",
                $"A national ID in this market is issued by {market.Options.Country}."));
        }
    }

    private static void Documents(List<DocumentInput> documents, int maxImageBytes, List<FieldError> errors, List<DecodedImage> images)
    {
        var maxBase64Length = (maxImageBytes + 2) / 3 * 4;
        for (var i = 0; i < documents.Count; i++)
        {
            var type = Normalize(documents[i].Type);
            var image = documents[i].Image;
            if (!DocumentTypes.All.Contains(type))
            {
                errors.Add(new FieldError($"documents[{i}].type", "documents.type", "Use PASSPORT, ID_CARD or SELFIE."));
                continue;
            }

            if (string.IsNullOrEmpty(image))
            {
                errors.Add(new FieldError($"documents[{i}].image", "documents.image.required", "Send the image as base64."));
                continue;
            }

            if (image.Length > maxBase64Length)
            {
                errors.Add(new FieldError($"documents[{i}].image", "documents.image.tooLarge", $"An image can be at most {maxImageBytes / (1024 * 1024)} MB."));
                continue;
            }

            var buffer = new byte[image.Length * 3 / 4 + 3];
            if (!Convert.TryFromBase64String(image, buffer, out var written) || written == 0)
            {
                errors.Add(new FieldError($"documents[{i}].image", "documents.image.base64", "The image isn't valid base64."));
                continue;
            }

            var bytes = buffer[..written];
            if (!IsJpegOrPng(bytes))
            {
                errors.Add(new FieldError($"documents[{i}].image", "documents.image.format", "Send a JPEG or PNG image."));
                continue;
            }

            images.Add(new DecodedImage(type, bytes, SHA256.HashData(bytes)));
        }
    }

    private static bool IsJpegOrPng(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? "";

    private static bool IsCountry(string value) => CountryPattern().IsMatch(value);

    [GeneratedRegex(@"^[\p{L}\p{M}' .\-]{1,100}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex PhonePattern();
}
