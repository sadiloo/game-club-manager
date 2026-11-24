using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetFirewallMergedApplyRunner
{
    internal static string BuildApplyOffScript(InternetGuardConfig config, bool throwAfterProfilesBeforeVerify = false)
        => InternetFirewallMergedOffScriptBuilder.BuildApplyOffScript(
            config.ServerIps,
            config.ServerPort,
            config.ExtraAllowPrograms,
            throwAfterProfilesBeforeVerify);

    internal static string ApplyOn(InternetGuardConfig config, int budgetMs)
    {
        var script = InternetFirewallMergedOffScriptBuilder.BuildLocalhostCleanupScript()
            + InternetFirewallMergedOffScriptBuilder.BuildResetScriptBody()
            + InternetFirewallMergedOffScriptBuilder.BuildReadbackEmitScript();
        var raw = InternetPowerShellRunner.Run(script, budgetMs, stepName: "apply_merged_on");
        return ClassifyMergedResult(raw, config, requireVerifiedStateFile: false, expectOn: true);
    }

    internal static string ApplyOff(InternetGuardConfig config, int budgetMs, bool throwAfterProfilesBeforeVerify = false)
    {
        var totalSw = Stopwatch.StartNew();
        if (config.ServerIps.Length == 0 || config.ServerPort <= 0)
        {
            return "firewall_not_configured";
        }

        var script = BuildApplyOffScript(config, throwAfterProfilesBeforeVerify);
        var telemetry = InternetPowerShellRunner.RunWithTelemetry(script, budgetMs, stepName: "apply_merged_off");
        var raw = telemetry.Output;
        var psStartMs = telemetry.PsStartMs;
        var phases = TryParseOffScriptPhases(telemetry.Stderr);
        var verifySw = Stopwatch.StartNew();
        var result = ClassifyMergedResult(raw, config, requireVerifiedStateFile: false, expectOn: false);
        var verifyMs = verifySw.ElapsedMilliseconds;
        GuardWatchdogLog.Append(
            "GUARD_OFF_PHASES total_ms="
            + totalSw.ElapsedMilliseconds
            + " cleanup_ms="
            + phases.cleanupMs
            + " off_body_ms="
            + phases.offBodyMs
            + " readback_ms="
            + phases.readbackMs
            + " verify_ms="
            + verifyMs
            + " ps_start_ms="
            + psStartMs);
        return result;
    }

    private static (string cleanupMs, string offBodyMs, string readbackMs) TryParseOffScriptPhases(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return ("na", "na", "na");
        }

        foreach (var line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var phasesIdx = line.IndexOf("PHASES ", StringComparison.Ordinal);
            if (phasesIdx < 0)
            {
                continue;
            }

            var phaseLine = line.Substring(phasesIdx);

            string cleanup = "na";
            string off = "na";
            string readback = "na";
            foreach (var part in phaseLine.Split(' '))
            {
                if (part.StartsWith("cleanup_ms=", StringComparison.Ordinal))
                {
                    cleanup = part.Substring("cleanup_ms=".Length);
                }
                else if (part.StartsWith("off_ms=", StringComparison.Ordinal))
                {
                    off = part.Substring("off_ms=".Length);
                }
                else if (part.StartsWith("readback_ms=", StringComparison.Ordinal))
                {
                    readback = part.Substring("readback_ms=".Length);
                }
            }

            return (cleanup, off, readback);
        }

        return ("na", "na", "na");
    }

    private static string ClassifyMergedResult(string raw, InternetGuardConfig config, bool requireVerifiedStateFile, bool expectOn)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "apply_failed:empty";
        }

        if (!raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            if (raw.StartsWith("apply_failed", StringComparison.Ordinal))
            {
                return raw;
            }

            return raw.Contains("firewall_script_ok", StringComparison.Ordinal) ? "applied" : raw;
        }

        try
        {
            var readback = InternetFirewallOffApplyReadback.ParseFromPowerShellJson(raw, config);
            if (expectOn)
            {
                return readback.Model.ClassifyOnReset() ?? "applied";
            }

            return readback.ClassifyOff(requireVerifiedStateFile) ?? "applied";
        }
        catch
        {
            return "apply_failed:readback_parse";
        }
    }
}