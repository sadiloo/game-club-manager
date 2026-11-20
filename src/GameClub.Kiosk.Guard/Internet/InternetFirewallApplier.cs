using System;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetFirewallApplier : IInternetFirewallApplier
{
    private readonly bool _throwAfterProfilesBeforeVerify;

    public InternetFirewallApplier()
        : this(InternetFirewallApplierTestInjections.None)
    {
    }

    internal InternetFirewallApplier(InternetFirewallApplierTestInjections testInjections)
    {
        _throwAfterProfilesBeforeVerify = testInjections.ThrowAfterProfilesBeforeVerify;
    }

    public string Apply(InternetGuardConfig config, bool on)
    {
        var budget = InternetGuardReconcilePolicy.ResolveApplyMergedBudgetMs();
        GuardWatchdogLog.Append($"apply_merged begin on={on} budget_ms={budget}");
        var result = on
            ? InternetFirewallMergedApplyRunner.ApplyOn(config, budget)
            : InternetFirewallMergedApplyRunner.ApplyOff(config, budget, _throwAfterProfilesBeforeVerify);
        GuardWatchdogLog.Append($"apply_merged end on={on} result={result}");
        return result;
    }

    public string RunResetScript() =>
        RunResetScriptInternal(InternetGuardReconcilePolicy.ResolveApplyResetScriptBudgetMs());

    internal string RunResetScriptForReconcile() =>
        RunResetScriptInternal(InternetGuardReconcilePolicy.ResolveResetScriptBudgetMs());

    private static string RunResetScriptInternal(int timeoutMs)
    {
        const string GroupName = "Checkpoint Internet Off";
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