namespace Atlas.Simulators.Control;

/// <summary>
/// One call a stand-in received. It holds the caller's idempotency key and the outcome, never the request body: the
/// simulator host keeps no personal data either.
/// </summary>
public sealed record JournalEntry(DateTimeOffset At, string Provider, string Operation, string? IdempotencyKey, int Status, string Outcome);

/// <summary>Every call the stand-ins received, newest last. Tests use it to count calls per application.</summary>
internal sealed class RequestJournal(TimeProvider time)
{
    private const int Capacity = 10_000;

    private readonly Lock _lock = new();
    private readonly Queue<JournalEntry> _entries = new();

    public void Record(Provider provider, string operation, string? idempotencyKey, int status, string outcome)
    {
        var entry = new JournalEntry(time.GetUtcNow(), provider.Name(), operation, idempotencyKey, status, outcome);
        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    /// <summary>Entries for one provider and idempotency-key prefix (an application ID, say); null matches everything.</summary>
    public IReadOnlyList<JournalEntry> Find(string? provider, string? keyPrefix)
    {
        lock (_lock)
        {
            return _entries
                .Where(e => provider is null || string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase))
                .Where(e => keyPrefix is null || (e.IdempotencyKey?.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
