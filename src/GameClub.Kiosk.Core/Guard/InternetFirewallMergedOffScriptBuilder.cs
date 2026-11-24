using System.Linq;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>PowerShell fragments for merged internet-OFF apply (shared with Guard; tested on Linux via Core.Tests).</summary>
internal static class InternetFirewallMergedOffScriptBuilder
{
    internal const string GroupName = "Checkpoint Internet Off";

    internal static string BuildApplyOffScript(
        string[] serverIps,
        int serverPort,
        string[] extraAllowPrograms,
        bool throwAfterProfilesBeforeVerify = false)
    {
        var extra = string.Join(";", extraAllowPrograms.Select(p => p.Replace("'", "''")));
        var ips = string.Join(",", serverIps.Select(ip => $"'{ip.Replace("'", "''")}'"));
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
        var offBody = BuildOffBody(ips, serverPort, extra, seamThrow);
        return phaseSwInit
            + BuildLocalhostCleanupScript()
            + phaseAfterCleanup
            + offBody
            + phaseAfterOffBody
            + BuildReadbackEmitScript()
            + phaseAfterReadback;
    }

    internal static string BuildOffBody(string ipsCsv, int serverPort, string extraProgramsSemicolon, string seamThrow)
        => $@"
$ErrorActionPreference = 'Stop'
$ips = @({ipsCsv})
$port = {serverPort}
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
if ('{extraProgramsSemicolon}' -ne '') {{
  foreach ($p in '{extraProgramsSemicolon}'.Split(';')) {{
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

    internal static string BuildResetScriptBody()
        => $@"
$ErrorActionPreference = 'Stop'
$group = '{GroupName}'
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction Stop
}}
'firewall_script_ok'
";

    internal static string BuildLocalhostCleanupScript()
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

    internal static string BuildReadbackEmitScript()
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
}