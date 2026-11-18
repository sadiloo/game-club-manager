using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallApplier
{
    private const string GroupName = "Checkpoint Internet Off";

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (on)
        {
            return RunResetScript();
        }

        if (string.Equals(
            Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT"),
            "1",
            StringComparison.Ordinal))
        {
            return "apply_failed:harness_injected";
        }

        if (config.ServerIps.Length == 0 || config.ServerPort <= 0)
        {
            return "firewall_not_configured";
        }

        var extra = string.Join(";", config.ExtraAllowPrograms.Select(p => p.Replace("'", "''")));
        var ips = string.Join(",", config.ServerIps.Select(ip => $"'{ip.Replace("'", "''")}'"));
        var script = $@"
$ErrorActionPreference = 'Stop'
$ips = @({ips})
$port = {config.ServerPort}
$group = '{GroupName}'
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -PolicyStore localhost -Name $prof -Enabled True -DefaultOutboundAction Block -AllowLocalFirewallRules False
}}
$existing = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction Stop)
if ($existing.Count -gt 0) {{
  $existing | Remove-NetFirewallRule -PolicyStore localhost -ErrorAction Stop
}}
New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - Server"" -Group $group -Direction Outbound -Action Allow -Protocol TCP -RemoteAddress $ips -RemotePort $port | Out-Null
New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - DHCP"" -Group $group -Direction Outbound -Action Allow -Protocol UDP -RemotePort 67-68 | Out-Null
if ('{extra}' -ne '') {{
  foreach ($p in '{extra}'.Split(';')) {{
    if ($p) {{ New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - $p"" -Group $group -Direction Outbound -Action Allow -Program $p | Out-Null }}
  }}
}}
$profiles = Get-NetFirewallProfile -PolicyStore localhost | Where-Object {{ $_.DefaultOutboundAction -ne 'Block' }}
if (@($profiles).Count -gt 0) {{
  throw ""apply_failed:verification:localhost_profiles_not_block""
}}
$rules = @(Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction Stop)
if ($rules.Count -lt 1) {{
  throw ""apply_failed:verification:missing_checkpoint_off_rules""
}}
'applied'
";
        return RunPowerShell(script);
    }

    public string RunResetScript()
    {
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Get-NetFirewallRule -PolicyStore localhost -Group $group | Remove-NetFirewallRule -PolicyStore localhost
foreach ($prof in @('Domain','Private','Public')) {{
  Set-NetFirewallProfile -PolicyStore localhost -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured
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

internal sealed class InternetGuardConfig
{
    public string StationId { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public string[] ServerIps { get; set; } = Array.Empty<string>();
    public string ServerPublicKeyPem { get; set; } = "";
    public string[] ExtraAllowPrograms { get; set; } = Array.Empty<string>();
}