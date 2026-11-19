using System;
using System.Linq;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;

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

    internal static string ApplyOff(InternetGuardConfig config, int budgetMs)
    {
        if (config.ServerIps.Length == 0 || config.ServerPort <= 0)
        {
            return "firewall_not_configured";
        }

        var extra = string.Join(";", config.ExtraAllowPrograms.Select(p => p.Replace("'", "''")));
        var ips = string.Join(",", config.ServerIps.Select(ip => $"'{ip.Replace("'", "''")}'"));
        var seamThrow = InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify
            ? "throw \"apply_failed:verification:test_seam_after_profiles\""
            : "";
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
New-NetFirewallRule -DisplayName ""$group - DHCP"" -Group $group -Direction Outbound -Action Allow -Protocol UDP -RemotePort 67-68 | Out-Null
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
        var script = BuildLocalhostCleanupScript() + offBody + BuildReadbackEmitScript();
        var raw = InternetPowerShellRunner.Run(script, budgetMs, stepName: "apply_merged_off");
        return ClassifyMergedResult(raw, config, requireVerifiedStateFile: true, expectOn: false);
    }

    private static string BuildLocalhostCleanupScript()
        => $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -PolicyStore localhost -ErrorAction SilentlyContinue
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