using System.Globalization;
using Atlas.Simulators.Control;

namespace Atlas.Simulators.Scenarios;

/// <summary>The ticket's World-Check request.</summary>
public sealed record WorldCheckRequest(string? Name, string? DateOfBirth, string? Nationality);

public sealed record WorldCheckMatch(string ListType, double Score, string Subject);

public sealed record WorldCheckResponse(string CaseId, string Status, IReadOnlyList<WorldCheckMatch> Matches);

/// <summary>
/// Stands in for Refinitiv World-Check. A name containing <c>MATCH</c> (in capitals) is a possible sanctions match,
/// one containing <c>PEP</c> a possible PEP match; anyone else is clear. The matched subjects are made-up list
/// entries.
/// </summary>
internal static class WorldCheckSimulator
{
    public static SimulatorAnswer Answer(WorldCheckRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return SimulatorAnswer.BadRequest("name is required.");
        }

        if (!DateOnly.TryParseExact(request.DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return SimulatorAnswer.BadRequest("dateOfBirth must be yyyy-MM-dd.");
        }

        if (request.Nationality is not { Length: 2 } nationality || !nationality.All(char.IsAsciiLetterUpper))
        {
            return SimulatorAnswer.BadRequest("nationality must be an ISO 3166 alpha-2 code.");
        }

        var matches = Evaluate(request.Name);
        var status = matches.Count == 0 ? "CLEAR" : "POSSIBLE_MATCH";
        var outcome = matches.Count == 0 ? status : $"{status} ({string.Join(", ", matches.Select(m => m.ListType))})";
        return new SimulatorAnswer(StatusCodes.Status200OK, new WorldCheckResponse($"wc_{Guid.NewGuid():N}"[..15], status, matches), outcome);
    }

    public static IReadOnlyList<WorldCheckMatch> Evaluate(string name)
    {
        var matches = new List<WorldCheckMatch>();
        if (name.Contains("MATCH", StringComparison.Ordinal))
        {
            matches.Add(new WorldCheckMatch("SANCTIONS", 0.82, "Sanctions list entry (test data)"));
        }

        if (name.Contains("PEP", StringComparison.Ordinal))
        {
            matches.Add(new WorldCheckMatch("PEP", 0.77, "Politically exposed person entry (test data)"));
        }

        return matches;
    }
}
