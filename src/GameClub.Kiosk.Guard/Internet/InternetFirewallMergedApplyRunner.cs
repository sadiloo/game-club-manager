using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetFirewallMergedApplyRunner
{
    private const string GroupName = "Checkpoint Internet Off";

    internal static string ApplyOn(InternetGuardConfig config, int budgetMs)
    {
        var script = BuildLocalhostCleanupScript()
            + BuildResetScriptBody()
            + BuildReadbackEmitScript();
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

        var extra = string.Join(";", config.ExtraAllowPrograms.Select(p => p.Replace("'", "''")));
        var ips = string.Join(",", config.ServerIps.Select(ip => $"'{ip.Replace("'", "''")}'"));
        var seamThrow = throwAfterProfilesBeforeVerify
            ? "throw \"apply_failed:verification:test_seam_after_profiles\""
            : "";
        const string phaseSwInit = @"
$__gcmPhaseSw = [System.Diagnostics.Stopwatch]::StartNew()
";
        const string phaseAfterCleanup = @"
$__gcmCleanupMs = $__gcmPhaseSw.ElapsedMilliseconds
$__gcmPhaseSw.Restart()
";
        const string phaseAfterOffBody = @"
$__gcmOffMs = $__gcmPhaseSw.ElapsedMilliseconds
$__gcmPhaseSw.Restart()
";
        const string phaseAfterReadback = @"
$__gcmReadbackMs = $__gcmPhaseSw.ElapsedMilliseconds
[Console]::Error.WriteLine(""PHASES cleanup_ms=$__gcmCleanupMs off_ms=$__gcmOffMs readback_ms=$__gcmReadbackMs"")
";
        var offBody = $@"
$ErrorActionPreference = 'Stop'
$ips = @({ips})
$port = {config.ServerPort}
$group = '{GroupName}'
try {{
  $existing = @(Get-NetFirewallRule -Group $group -ErrorAction Stop)
}} catch {{
  if ($_.CategoryInfo.Category -eq 'ObjectNotFound' -or $_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') {{
    $existing = @()
  }} else {{
    throw
  }}
}}
if ($existing.Count -gt 0) {{
  $existing | Remove-NetFirewallRule -ErrorAction Stop
}}
New-NetFirewallRule -DisplayName ""$group - Server"" -Group $group -Direction Outbound -Action Allow -Protocol TCP -RemoteAddress $ips -RemotePort $port | Out-Null
{InternetFirewallDhcpAllowRule.NewNetFirewallRuleInvocation}
if ('{extra}' -ne '') {{
  foreach ($p in '{extra}'.Split(';')) {{
    if ($p) {{ New-NetFirewallRule -DisplayName ""$group - $p"" -Group $group -Direction Outbound -Action Allow -Program $p | Out-Null }}
  }}
}}
$profilesBlocked = $false
try {{
  foreach ($prof in @('Domain','Private','Public')) {{
    Set-NetFirewallProfile -Name $prof -Enabled True -DefaultOutboundAction Block -AllowLocalFirewallRules False -ErrorAction Stop
  }}
  $profilesBlocked = $true
  {seamThrow}
}} catch {{
  if ($profilesBlocked) {{
    foreach ($prof in @('Domain','Private','Public')) {{
      Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction SilentlyContinue
    }}
  }}
  try {{
    Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
  }} catch {{ }}
  throw
}}
'firewall_script_ok'
";
        var script = phaseSwInit
            + BuildLocalhostCleanupScript()
            + phaseAfterCleanup
            + offBody
            + phaseAfterOffBody
            + BuildReadbackEmitScript()
            + phaseAfterReadback;
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

    private static string BuildLocalhostCleanupScript()
        => $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Remove-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue
foreach ($prof in @('Domain','Private','Public')) {{
  $p = Get-NetFirewallProfile -PolicyStore localhost -Name $prof -ErrorAction SilentlyContinue
  if ($p -and $p.DefaultOutboundAction.ToString() -eq 'Block') {{
    Set-NetFirewallProfile -PolicyStore localhost -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction SilentlyContinue
  }}
}}
";

    private static string BuildResetScriptBody()
        => $@"
$ErrorActionPreference = 'Stop'
$group = '{GroupName}'
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction Stop
}}
'firewall_script_ok'
";

    private static string BuildReadbackEmitScript()
        => $@"
$group = '{GroupName}'
$activeOut = @()
$localOut = @()
foreach ($prof in @('Domain','Private','Public')) {{
  $a = Get-NetFirewallProfile -Name $prof -ErrorAction SilentlyContinue
  $activeOut += if ($a) {{ $a.DefaultOutboundAction.ToString() }} else {{ 'missing' }}
  $l = Get-NetFirewallProfile -PolicyStore localhost -Name $prof -ErrorAction SilentlyContinue
  $localOut += if ($l) {{ $l.DefaultOutboundAction.ToString() }} else {{ 'missing' }}
}}
$activeRules = @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue)
$localRules = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue)
@{{ activeOut = $activeOut; localOut = $localOut; activeRuleCount = $activeRules.Count; localRuleCount = $localRules.Count }} | ConvertTo-Json -Compress
";

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