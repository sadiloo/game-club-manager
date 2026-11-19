using GameClub.Server.Testing;

namespace GameClub.Server.TestHost;

public static class PcSpecialBandTestHostArgs
{
    public static (string[] HostArgs, PcSpecialBandHarnessOptions? Harness) Parse(string[] argv)
        => PcSpecialBandHostArgs.Parse(argv);
}