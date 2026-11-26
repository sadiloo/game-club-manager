using System.Linq;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelOfflineFlashAssertTests
{
    public static TheoryData<string, string> ReviewSequenceCases => new()
    {
        { "steady offline", new string('O', 89) },
        { "panel 4 samples then steady offline (detection latency)", new string('P', 4) + new string('O', 85) },
        { "CI failure shape R20 fixes: panel 8 then offline (81/89)", new string('P', 8) + new string('O', 81) },
        { "alternating O/P (flash loop)", string.Concat(Enumerable.Repeat("OP", 45)) },
        { "single 1-sample flash mid-window", new string('O', 44) + "P" + new string('O', 44) },
        { "3 short 1-sample flashes", new string('O', 20) + "P" + new string('O', 20) + "P" + new string('O', 20) + "P" + new string('O', 26) },
        { "flash at very end", new string('O', 88) + "P" },
        { "panel visible 40 of 89 then offline", new string('P', 40) + new string('O', 49) },
        { "panel visible 80 of 89 then offline (last 9 offline)", new string('P', 80) + new string('O', 9) },
        { "offline <-> error/blank page loop (X)", string.Concat(Enumerable.Repeat("OX", 45)) },
        { "offline then blank/error for rest (10 O then 79 X)", new string('O', 10) + new string('X', 79) },
        { "offline <-> blank loop every 3rd sample", string.Concat(Enumerable.Repeat("OOX", 30))[..89] },
        { "webview null for most (N), 10 O at end", new string('N', 79) + new string('O', 10) },
        { "two panel cycles", new string('P', 2) + new string('O', 20) + new string('P', 2) + new string('O', 20) + new string('P', 2) + new string('O', 41) },
    };

    [Theory]
    [MemberData(nameof(ReviewSequenceCases))]
    public void reviewer_sim_antiflash_sequences_match_expected_verdict(string caseName, string sequence)
    {
        var expectedPass = ExpectedPassForCase(caseName);
        var samples = PanelOfflineFlashAssert.SamplesFromSimSequence(sequence);
        var result = PanelOfflineFlashAssert.Evaluate(samples);
        Assert.True(
            expectedPass == result.Passes,
            $"{caseName}: expected={(expectedPass ? "PASS" : "fail")} actual={(result.Passes ? "PASS" : "fail")} reason={result.FailureReason}");
    }

    [Fact]
    public void ci_shape_81_of_89_offline_samples_must_fail()
    {
        var sequence = new string('P', 8) + new string('O', 81);
        var result = PanelOfflineFlashAssert.Evaluate(PanelOfflineFlashAssert.SamplesFromSimSequence(sequence));
        Assert.False(result.Passes);
        Assert.Contains("offline/total", result.FailureReason, StringComparison.Ordinal);
    }

    private static bool ExpectedPassForCase(string caseName) => caseName switch
    {
        "steady offline" => true,
        "panel 4 samples then steady offline (detection latency)" => true,
        "CI failure shape R20 fixes: panel 8 then offline (81/89)" => false,
        "alternating O/P (flash loop)" => false,
        "single 1-sample flash mid-window" => true,
        "3 short 1-sample flashes" => false,
        "flash at very end" => false,
        "panel visible 40 of 89 then offline" => false,
        "panel visible 80 of 89 then offline (last 9 offline)" => false,
        "offline <-> error/blank page loop (X)" => false,
        "offline then blank/error for rest (10 O then 79 X)" => false,
        "offline <-> blank loop every 3rd sample" => false,
        "webview null for most (N), 10 O at end" => false,
        "two panel cycles" => false,
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "unexpected reviewer case"),
    };
}