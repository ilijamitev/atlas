using System.Text;
using Atlas.Simulators;
using Atlas.Simulators.Control;
using Atlas.Simulators.Scenarios;
using Microsoft.Extensions.Options;

namespace Atlas.UnitTests.Simulators;

/// <summary>The stand-ins' scenarios: what each trigger produces.</summary>
public sealed class SimulatorScenarioTests
{
    [Theory]
    [InlineData("", "", "VALID", true, 0.97)]
    [InlineData("INVALID", "", "INVALID", false, 0.30)]
    [InlineData("INCONCLUSIVE", "", "INCONCLUSIVE", true, 0.55)]
    [InlineData("", "FACE_MISMATCH", "VALID", false, 0.41)]
    [InlineData("LOW_CONFIDENCE", "", "VALID", true, 0.62)]
    public void IDNow_reads_the_marker_in_either_image(string documentMarker, string selfieMarker, string result, bool faceMatch, double confidence)
    {
        var answer = IdNowSimulator.Evaluate(Image(documentMarker), Image(selfieMarker));

        Assert.Equal((result, faceMatch, confidence), (answer.DocumentResult, answer.FaceMatch, answer.Confidence));
    }

    [Theory]
    [InlineData("Test Tester", new string[0])]
    [InlineData("Test MATCH", new[] { "SANCTIONS" })]
    [InlineData("Test PEP", new[] { "PEP" })]
    [InlineData("Test PEP MATCH", new[] { "SANCTIONS", "PEP" })]
    [InlineData("Pepper Matchett", new string[0])]
    public void World_Check_matches_only_the_markers_in_capitals(string name, string[] listTypes)
    {
        Assert.Equal(listTypes, WorldCheckSimulator.Evaluate(name).Select(m => m.ListType));
    }

    [Fact]
    public void FailNext_counts_down_before_the_mode_applies_again()
    {
        var faults = new FaultControl(Options.Create(new SimulatorOptions()));
        faults.Set(Provider.IdNow, new FaultSettings(FaultMode.Healthy, 2, LatencyMs: 0));

        var kinds = Enumerable.Range(0, 3).Select(_ => faults.Next(Provider.IdNow).Kind).ToList();

        Assert.Equal([FaultKind.Unavailable, FaultKind.Unavailable, FaultKind.None], kinds);
        Assert.Equal(0, faults.Get(Provider.IdNow).FailNext);
    }

    [Theory]
    [InlineData(FaultMode.Down, "Unavailable")]
    [InlineData(FaultMode.Error500, "ServerError")]
    [InlineData(FaultMode.Timeout, "Hang")]
    [InlineData(FaultMode.Reject, "Refuse")]
    public void Each_mode_fails_its_own_way_and_only_for_its_provider(FaultMode mode, string kind)
    {
        var faults = new FaultControl(Options.Create(new SimulatorOptions()));
        faults.Set(Provider.WorldCheck, new FaultSettings(mode, 0, null));

        Assert.Equal(kind, faults.Next(Provider.WorldCheck).Kind.ToString());
        Assert.Equal(FaultKind.None, faults.Next(Provider.IdNow).Kind);
    }

    [Fact]
    public void Latency_defaults_per_provider_and_slow_outlasts_the_eight_second_wait()
    {
        var faults = new FaultControl(Options.Create(new SimulatorOptions()));
        faults.Set(Provider.CoreBanking, new FaultSettings(FaultMode.Slow, 0, null));

        Assert.Equal(TimeSpan.FromSeconds(2), faults.Next(Provider.IdNow).Delay);
        Assert.Equal(TimeSpan.FromSeconds(1), faults.Next(Provider.WorldCheck).Delay);
        Assert.Equal(TimeSpan.FromSeconds(9), faults.Next(Provider.CoreBanking).Delay);
    }

    private static byte[] Image(string marker) => [0xFF, 0xD8, 0xFF, .. Encoding.ASCII.GetBytes($"ATLAS TEST IMAGE {marker}"), 0xFF, 0xD9];
}
