using GameClub.Server.Testing;

namespace GameClub.Server.TestHost;

public static class PcSpecialBandTestHostArgs
{
    public static (string[] HostArgs, PcSpecialBandHarnessOptions? Harness) Parse(string[] argv)
    {
        var hostArgs = new List<string>();
        PcSpecialBandHarnessOptions? harness = null;
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (string.Equals(arg, "--pc-band", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= argv.Length)
                {
                    throw new ArgumentException("--pc-band requires day or night.");
                }

                harness = PcSpecialBandHarnessSchedule.OptionsForBand(argv[++i]);
                continue;
            }

            if (string.Equals(arg, "--pc-normal-start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--pc-normal-end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--pc-vip-start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--pc-vip-end", StringComparison.OrdinalIgnoreCase))
            {
                harness ??= new PcSpecialBandHarnessOptions { Enabled = true };
                if (i + 1 >= argv.Length)
                {
                    throw new ArgumentException($"{arg} requires HH:mm.");
                }

                var value = argv[++i];
                if (arg.EndsWith("normal-start", StringComparison.OrdinalIgnoreCase))
                {
                    harness.PcNormalStart = value;
                }
                else if (arg.EndsWith("normal-end", StringComparison.OrdinalIgnoreCase))
                {
                    harness.PcNormalEnd = value;
                }
                else if (arg.EndsWith("vip-start", StringComparison.OrdinalIgnoreCase))
                {
                    harness.PcVipStart = value;
                }
                else
                {
                    harness.PcVipEnd = value;
                }

                continue;
            }

            hostArgs.Add(arg);
        }

        return (hostArgs.ToArray(), harness);
    }
}