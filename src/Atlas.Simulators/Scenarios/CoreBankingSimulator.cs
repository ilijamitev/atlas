using System.Collections.Concurrent;
using System.Security.Cryptography;
using Atlas.Simulators.Control;

namespace Atlas.Simulators.Scenarios;

public sealed record AccountHolder(string? FirstName, string? LastName, string? DateOfBirth);

public sealed record AccountRequest(Guid ApplicationId, string? Market, AccountHolder? Holder);

public sealed record AccountResponse(string AccountId);

public sealed record CardRequest(Guid ApplicationId, string? AccountId);

public sealed record CardResponse(string CardId, string Status);

/// <summary>
/// Stands in for core banking, which the ticket doesn't specify. Both calls are idempotent by application ID: a
/// repeat returns 200 with the account or card already created, never a second one (AC6).
/// </summary>
internal sealed class CoreBankingSimulator
{
    private readonly ConcurrentDictionary<Guid, string> _accounts = new();
    private readonly ConcurrentDictionary<Guid, string> _cards = new();

    public SimulatorAnswer OpenAccount(AccountRequest request)
    {
        if (request.ApplicationId == Guid.Empty || request.Market is not { Length: 2 })
        {
            return SimulatorAnswer.BadRequest("applicationId and market are required.");
        }

        if (request.Holder is not { FirstName.Length: > 0, LastName.Length: > 0, DateOfBirth.Length: > 0 })
        {
            return SimulatorAnswer.BadRequest("holder needs firstName, lastName and dateOfBirth.");
        }

        var candidate = NewId("acc");
        var accountId = _accounts.GetOrAdd(request.ApplicationId, candidate);
        var created = ReferenceEquals(accountId, candidate);
        return new SimulatorAnswer(
            created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            new AccountResponse(accountId),
            created ? "account opened" : "account already open");
    }

    public SimulatorAnswer OrderCard(CardRequest request)
    {
        if (!_accounts.TryGetValue(request.ApplicationId, out var accountId) || accountId != request.AccountId)
        {
            return new SimulatorAnswer(
                StatusCodes.Status422UnprocessableEntity,
                new { error = "UNKNOWN_ACCOUNT" },
                "refused: no such account for this application");
        }

        var candidate = NewId("card");
        var cardId = _cards.GetOrAdd(request.ApplicationId, candidate);
        var created = ReferenceEquals(cardId, candidate);
        return new SimulatorAnswer(
            created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
            new CardResponse(cardId, "ORDERED"),
            created ? "card ordered" : "card already ordered");
    }

    private static string NewId(string prefix) => $"{prefix}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
}
