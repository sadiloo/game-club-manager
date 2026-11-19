using GameClub.Server;
using GameClub.Server.TestHost;
using GameClub.Server.Testing;

var (hostArgs, harness) = PcSpecialBandTestHostArgs.Parse(args);
if (harness is not { Enabled: true })
{
    Console.Error.WriteLine("GameClub.Server.TestHost requires --pc-band day or night.");
    return 2;
}

var bandMode = PcBandHarnessDiagnostics.BandModeLabelForHostArgs(string.Join(' ', args));
Console.WriteLine(PcBandHarnessDiagnostics.FormatLogLine(bandMode, harness));
PcBandHarnessDiagnostics.AssertLiveTestHarnessActive(harness);

await ServerApplication.RunAsync(hostArgs, builder =>
{
    builder.Services.AddHostedService<LeaseKeyFingerprintStartup>();
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

return 0;