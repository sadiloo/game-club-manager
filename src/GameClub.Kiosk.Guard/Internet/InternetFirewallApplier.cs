using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallApplier : IInternetFirewallApplier
{
    private const string GroupName = "Checkpoint Internet Off";

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (on)
        {
            var result = RunResetScript();
            TryDeleteVerifiedState();
            return result;
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
  $existing = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction Stop)
}} catch {{
  if ($_.CategoryInfo.Category -eq 'ObjectNotFound' -or $_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') {{
    $existing = @()
  }} else {{
    throw
  }}
}}
if ($existing.Count -gt 0) {{
  $existing | Remove-NetFirewallRule -PolicyStore localhost -ErrorAction Stop
}}
$profilesBlocked = $false
try {{
  foreach ($prof in @('Domain','Private','Public')) {{
    Set-NetFirewallProfile -Name $prof -Enabled True -DefaultOutboundAction Block -AllowLocalFirewallRules False -ErrorAction Stop
    Set-NetFirewallProfile -PolicyStore localhost -Name $prof -Enabled True -DefaultOutboundAction Block -AllowLocalFirewallRules False -ErrorAction Stop
  }}
  $profilesBlocked = $true
  {seamThrow}
  foreach ($prof in @('Domain','Private','Public')) {{
    $active = Get-NetFirewallProfile -Name $prof -ErrorAction Stop
    if ($active.DefaultOutboundAction.ToString() -ne 'Block') {{
      throw ""apply_failed:verification:active_profiles_not_block""
    }}
    $local = Get-NetFirewallProfile -PolicyStore localhost -Name $prof -ErrorAction Stop
    if ($local.DefaultOutboundAction.ToString() -ne 'Block') {{
      throw ""apply_failed:verification:localhost_profiles_not_block""
    }}
  }}
  New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - Server"" -Group $group -Direction Outbound -Action Allow -Protocol TCP -RemoteAddress $ips -RemotePort $port | Out-Null
  New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - DHCP"" -Group $group -Direction Outbound -Action Allow -Protocol UDP -RemotePort 67-68 | Out-Null
  if ('{extra}' -ne '') {{
    foreach ($p in '{extra}'.Split(';')) {{
      if ($p) {{ New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - $p"" -Group $group -Direction Outbound -Action Allow -Program $p | Out-Null }}
    }}
  }}
  $rules = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction Stop)
  if ($rules.Count -lt 1) {{
    throw ""apply_failed:verification:missing_checkpoint_off_rules""
  }}
  'applied'
}} catch {{
  if ($profilesBlocked) {{
    foreach ($prof in @('Domain','Private','Public')) {{
      Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction SilentlyContinue
      Set-NetFirewallProfile -PolicyStore localhost -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction SilentlyContinue
    }}
  }}
  try {{
    Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue |
      Remove-NetFirewallRule -PolicyStore localhost -ErrorAction SilentlyContinue
  }} catch {{ }}
  throw
}}
";
        var powerShellResult = RunPowerShell(script);
        if (!string.Equals(powerShellResult, "applied", StringComparison.Ordinal))
        {
            return powerShellResult;
        }

        TryPersistVerifiedOffState(config);
        if (!File.Exists(InternetGuardPaths.StatePath))
        {
            return "apply_failed:verification:state_missing";
        }

        return "applied";
    }

    public string RunResetScript()
    {
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Get-NetFirewallRule -PolicyStore localhost -Group $group | Remove-NetFirewallRule -PolicyStore localhost
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured
  Set-NetFirewallProfile -PolicyStore localhost -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured
}}
'applied'
";
        return RunPowerShell(script);
    }

    private static void TryPersistVerifiedOffState(InternetGuardConfig config)
    {
        try
        {
            var payload = new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, payload);
        }
        catch
        {
            /* classified on re-read */
        }
    }

    private static void TryDeleteVerifiedState()
    {
        try
        {
            if (File.Exists(InternetGuardPaths.StatePath))
            {
                File.Delete(InternetGuardPaths.StatePath);
            }
        }
        catch
        {
            /* best effort */
        }
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