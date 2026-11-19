using GameClub.Server;
using GameClub.Server.TestHost;
using GameClub.Server.Testing;

var (hostArgs, harness) = PcSpecialBandTestHostArgs.Parse(args);
var bandMode = PcBandHarnessDiagnostics.TryParseBandModeFromHostArgs(string.Join(' ', args)) ?? "production";
Console.WriteLine(PcBandHarnessDiagnostics.FormatLogLine(bandMode, harness));
if (harness is { Enabled: true })
{
    PcBandHarnessDiagnostics.AssertLiveTestHarnessActive(harness);
}

await ServerApplication.RunAsync(hostArgs, builder =>
{
    if (harness is { Enabled: true })
    {
        builder.Services.Configure<PcSpecialBandHarnessOptions>(_ =>
        {
            _.Enabled = true;
            _.PcNormalStart = harness.PcNormalStart;
            _.PcNormalEnd = harness.PcNormalEnd;
            _.PcVipStart = harness.PcVipStart;
            _.PcVipEnd = harness.PcVipEnd;
        });
    }
});