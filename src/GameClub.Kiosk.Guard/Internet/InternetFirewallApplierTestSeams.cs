namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Test-only hooks for firewall OFF script. Defaults off; never set from production paths.</summary>
internal static class InternetFirewallApplierTestSeams
{
    internal static bool ThrowAfterProfilesBeforeVerify;
}