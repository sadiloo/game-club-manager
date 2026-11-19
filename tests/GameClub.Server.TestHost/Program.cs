using GameClub.Server;
using GameClub.Server.TestHost;
using GameClub.Server.Testing;

var (hostArgs, harness) = PcSpecialBandTestHostArgs.Parse(args);

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