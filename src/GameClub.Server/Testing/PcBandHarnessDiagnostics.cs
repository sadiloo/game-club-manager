namespace GameClub.Server.Testing;

/// <summary>CI log lines and guards so live tests never run against production PC special-band hours.</summary>
public static class PcBandHarnessDiagnostics
{
    public const string ProdPcNormalStart = "00:00";
    public const string ProdPcNormalEnd = "08:30";

    public static string FormatLogLine(string bandMode, PcSpecialBandHarnessOptions? harness)
    {
        if (harness is { Enabled: true })
        {
            return $"pc_band={bandMode} start={harness.PcNormalStart} end={harness.PcNormalEnd}";
        }

        return $"pc_band=production start={ProdPcNormalStart} end={ProdPcNormalEnd}";
    }

    public static string? TryParseBandModeFromHostArgs(string? hostArgs)
    {
        if (string.IsNullOrWhiteSpace(hostArgs))
        {
            return null;
        }

        var parts = hostArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "--pc-band", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return null;
    }

    public static string BandModeLabelForHostArgs(string? hostArgs)
    {
        var mode = TryParseBandModeFromHostArgs(hostArgs);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode!;
        }

        var (_, harness) = PcSpecialBandHostArgs.ParseFromCommandLine(hostArgs);
        return harness is { Enabled: true } ? "explicit" : "production";
    }

    public static PcSpecialBandHarnessOptions? OptionsFromHostArgs(string? hostArgs)
    {
        var (_, harness) = PcSpecialBandHostArgs.ParseFromCommandLine(hostArgs);
        if (harness is not { Enabled: true })
        {
            return null;
        }

        if (TryParseBandModeFromHostArgs(hostArgs) is { } mode)
        {
            return PcSpecialBandHarnessSchedule.OptionsForBand(mode);
        }

        return harness;
    }

    public static void LogLiveTestServerStart(string source, string? hostArgs)
    {
        var mode = BandModeLabelForHostArgs(hostArgs);
        var harness = OptionsFromHostArgs(hostArgs);
        var line = FormatLogLine(mode, harness);
        Console.WriteLine($"{source} {line}");
        AssertLiveTestHarnessActive(harness);
    }

    public static void AssertLiveTestHarnessActive(PcSpecialBandHarnessOptions? harness)
    {
        if (harness is not { Enabled: true })
        {
            throw new InvalidOperationException(
                "Live-test server must start GameClub.Server.TestHost with --pc-band (day or night); production PC band is not allowed.");
        }

        if (string.Equals(harness.PcNormalStart, ProdPcNormalStart, StringComparison.Ordinal)
            && string.Equals(harness.PcNormalEnd, ProdPcNormalEnd, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Live-test server must not use production pc special band 00:00-08:30.");
        }
    }
}