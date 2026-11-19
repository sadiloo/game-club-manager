using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallApplier : IInternetFirewallApplier
{
    private const string GroupName = "Checkpoint Internet Off";

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (on)
        {
            return RunResetScript();
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
        var powerShellResult = RunPowerShell(script);
        if (!string.Equals(powerShellResult, "firewall_script_ok", StringComparison.Ordinal))
        {
            return powerShellResult;
        }

        var readback = InternetFirewallOffApplyReadback.Capture(config);
        var failed = readback.ClassifyOff(requireVerifiedStateFile: false);
        return failed ?? "applied";
    }

    public string RunResetScript()
    {
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Get-NetFirewallRule -Group $group | Remove-NetFirewallRule
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured
}}
'applied'
";
        return RunPowerShell(script);
    }

    private static string RunPowerShell(string script)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell start failed");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            if (proc.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = $"exit_{proc.ExitCode}";
                }

                return detail.StartsWith("apply_failed", StringComparison.Ordinal)
                    ? detail
                    : $"apply_failed:powershell_error:{detail}";
            }

            var tail = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            if (string.IsNullOrEmpty(tail))
            {
                return "apply_failed:powershell_error:empty_output";
            }

            return tail!;
        }
        catch (Exception ex)
        {
            return $"apply_failed:powershell_error:{ex.Message}";
        }
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