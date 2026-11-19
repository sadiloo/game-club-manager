using System;
using System.Linq;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallApplier : IInternetFirewallApplier
{
    private const string GroupName = "Checkpoint Internet Off";

    public string Apply(InternetGuardConfig config, bool on)
    {
        InternetFirewallLocalhostStoreCleanup.CleanupLegacyLocalhostArtifacts(
            InternetGuardReconcilePolicy.ResolveApplyLocalhostCleanupBudgetMs());
        if (on)
        {
            var scriptResult = RunResetScriptInternal(InternetGuardReconcilePolicy.ResolveApplyResetScriptBudgetMs());
            if (!string.Equals(scriptResult, "firewall_script_ok", StringComparison.Ordinal))
            {
                return scriptResult;
            }

            var onReadback = InternetFirewallOffApplyReadback.Capture(
                config,
                InternetGuardReconcilePolicy.ResolveApplyReadbackBudgetMs());
            var onFailed = onReadback.Model.ClassifyOnReset();
            return onFailed ?? "applied";
        }

        if (config.ServerIps.Length == 0 || config.ServerPort <= 0)
        {
            return "firewall_not_configured";
        }

        var extra = string.Join(";", config.ExtraAllowPrograms.Select(p => p.Replace("'", "''")));
        var ips = string.Join(",", config.ServerIps.Select(ip => $"'{ip.Replace("'", "''")}'"));
        var seamThrow = InternetFirewallApplierTestSeams.ThrowAfterProfilesBeforeVerify
            ? "throw \"apply_failed:verification:test_seam_after_profiles\""
            : "";
        var script = $@"
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
  'firewall_script_ok'
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
";
        var powerShellResult = InternetPowerShellRunner.Run(
            script,
            InternetGuardReconcilePolicy.ResolveApplyOffScriptBudgetMs(),
            stepName: "apply_off");
        if (!string.Equals(powerShellResult, "firewall_script_ok", StringComparison.Ordinal))
        {
            return powerShellResult;
        }

        var readback = InternetFirewallOffApplyReadback.Capture(
            config,
            InternetGuardReconcilePolicy.ResolveApplyReadbackBudgetMs());
        var failed = readback.ClassifyOff(requireVerifiedStateFile: false);
        return failed ?? "applied";
    }

    public string RunResetScript() =>
        RunResetScriptInternal(InternetGuardReconcilePolicy.ResolveApplyResetScriptBudgetMs());

    internal string RunResetScriptForReconcile() =>
        RunResetScriptInternal(InternetGuardReconcilePolicy.ResolveResetScriptBudgetMs());

    private static string RunResetScriptInternal(int timeoutMs)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$group = '{GroupName}'
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction Stop
}}
'firewall_script_ok'
";
        return InternetPowerShellRunner.Run(script, timeoutMs, stepName: "reset_script");
    }
}

public sealed class InternetGuardConfig
{
    public string StationId { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public string[] ServerIps { get; set; } = Array.Empty<string>();
    public string ServerPublicKeyPem { get; set; } = "";
    public string[] ExtraAllowPrograms { get; set; } = Array.Empty<string>();
}