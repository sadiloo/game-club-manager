namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Removes legacy localhost-store OFF artifacts from builds that used -PolicyStore localhost (PR40-R15 option A).</summary>
internal static class InternetFirewallLocalhostStoreCleanup
{
    private const string GroupName = "Checkpoint Internet Off";

    public static void CleanupLegacyLocalhostArtifacts()
    {
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$group = '{GroupName}'
Get-NetFirewallRule -PolicyStore localhost -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -PolicyStore localhost -ErrorAction SilentlyContinue
foreach ($prof in @('Domain','Private','Public')) {{
  $p = Get-NetFirewallProfile -PolicyStore localhost -Name $prof -ErrorAction SilentlyContinue
  if ($p -and $p.DefaultOutboundAction.ToString() -eq 'Block') {{
    Set-NetFirewallProfile -PolicyStore localhost -Name $prof -DefaultOutboundAction NotConfigured -AllowLocalFirewallRules NotConfigured -ErrorAction SilentlyContinue
  }}
}}
'cleanup_ok'
";
        _ = InternetPowerShellRunner.Run(script, 60_000);
    }
}