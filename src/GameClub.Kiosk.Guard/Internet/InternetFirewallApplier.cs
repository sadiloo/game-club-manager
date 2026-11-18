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
  Set-NetFirewallProfile -PolicyStore localhost -Name $prof -Enabled True -DefaultOutboundAction Block -AllowLocalFirewallRules False -ErrorAction SilentlyContinue
}}
Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -PolicyStore localhost -ErrorAction SilentlyContinue
New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - Server"" -Group $group -Direction Outbound -Action Allow -Protocol TCP -RemoteAddress $ips -RemotePort $port | Out-Null
New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - DHCP"" -Group $group -Direction Outbound -Action Allow -Protocol UDP -RemotePort 67-68 | Out-Null
if ('{extra}' -ne '') {{
  foreach ($p in '{extra}'.Split(';')) {{
    if ($p) {{ New-NetFirewallRule -PolicyStore localhost -DisplayName ""$group - $p"" -Group $group -Direction Outbound -Action Allow -Program $p | Out-Null }}
  }}
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
            proc.WaitForExit(120_000);
            if (proc.ExitCode != 0)
            {
                return $"apply_failed:powershell_error:{proc.StandardError.ReadToEnd().Trim()}";
            }

            var tail = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(tail) ? "applied" : tail!;
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