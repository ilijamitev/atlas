using System.Security.Cryptography;
using System.Text;

namespace Atlas.IntegrationTests.Support;

/// <summary>Made-up applicants. Nothing here is, or looks like, a real person's data.</summary>
internal static class TestData
{
    public const string FirstName = "Test";
    public const string DateOfBirth = "1990-01-01";

    /// <summary>Each market's placeholder country and a made-up identifier that fits its rule in atlas.json.</summary>
    public static readonly IReadOnlyDictionary<string, (string Country, string Identifier)> Markets = new Dictionary<string, (string, string)>
    {
        ["MA"] = ("XA", "1234567890123"),
        ["MB"] = ("XB", "9876543210987"),
        ["MD"] = ("XD", "1234567890"),
    };

    /// <summary>
    /// A tiny file that passes as a JPEG: the JPEG start marker, a plain-text marker the IDNow simulator reacts to, random
    /// bytes so no two images are alike, and the end marker.
    /// </summary>
    public static string Image(string? marker = null) =>
        Convert.ToBase64String([
            0xFF, 0xD8, 0xFF, 0xE0,
            .. Encoding.ASCII.GetBytes($"ATLAS TEST IMAGE {marker}"),
            .. RandomNumberGenerator.GetBytes(16),
            0xFF, 0xD9,
        ]);

    public static Dictionary<string, object?> Complete(string market, string lastName = "Tester", string? documentMarker = null, string? selfieMarker = null)
    {
        var (country, identifier) = Markets[market];
        return new Dictionary<string, object?>
        {
            ["firstName"] = FirstName,
            ["lastName"] = lastName,
            ["dateOfBirth"] = DateOfBirth,
            ["nationality"] = country,
            ["countryOfResidence"] = country,
            ["identifier"] = new { type = "NATIONAL_ID", value = identifier, issuingCountry = country },
            ["email"] = "applicant@example.test",
            ["phone"] = "+15550100100",
            ["documents"] = new[]
            {
                new { type = "PASSPORT", image = Image(documentMarker) },
                new { type = "SELFIE", image = Image(selfieMarker) },
            },
            ["termsAccepted"] = true,
        };
    }

    /// <summary>The same applicant with only the given fields, for save-and-resume.</summary>
    public static Dictionary<string, object?> Only(Dictionary<string, object?> body, params string[] fields) =>
        body.Where(field => fields.Contains(field.Key)).ToDictionary(field => field.Key, field => field.Value);

    public static Dictionary<string, object?> Without(Dictionary<string, object?> body, params string[] fields) =>
        body.Where(field => !fields.Contains(field.Key)).ToDictionary(field => field.Key, field => field.Value);
}
