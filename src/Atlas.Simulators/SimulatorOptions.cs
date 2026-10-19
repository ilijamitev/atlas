namespace Atlas.Simulators;

/// <summary>The three systems the simulator host stands in for.</summary>
public enum Provider
{
    IdNow,
    WorldCheck,
    CoreBanking,
}

/// <summary>How long each stand-in takes to answer when healthy. The integration tests set them all to zero.</summary>
public sealed class SimulatorOptions
{
    public int IdNowLatencyMs { get; set; } = 2000;
    public int WorldCheckLatencyMs { get; set; } = 1000;
    public int CoreBankingLatencyMs { get; set; } = 200;

    internal TimeSpan Latency(Provider provider) => TimeSpan.FromMilliseconds(provider switch
    {
        Provider.IdNow => IdNowLatencyMs,
        Provider.WorldCheck => WorldCheckLatencyMs,
        _ => CoreBankingLatencyMs,
    });
}

internal static class ProviderNames
{
    public static string Name(this Provider provider) => provider switch
    {
        Provider.IdNow => "idnow",
        Provider.WorldCheck => "worldcheck",
        _ => "corebanking",
    };

    public static bool TryParse(string? value, out Provider provider)
    {
        foreach (var candidate in Enum.GetValues<Provider>())
        {
            if (string.Equals(candidate.Name(), value, StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        provider = default;
        return false;
    }
}
