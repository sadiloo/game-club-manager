namespace GameClub.Kiosk.Guard.Internet;

/// <summary>
/// Test-only per-applier OFF apply hooks (see InternalsVisibleTo integration tests).
/// Production constructs <see cref="InternetFirewallApplier"/> without this type.
/// </summary>
internal sealed class InternetFirewallApplierTestInjections
{
    internal bool ThrowAfterProfilesBeforeVerify { get; }

    internal InternetFirewallApplierTestInjections(bool throwAfterProfilesBeforeVerify = false)
    {
        ThrowAfterProfilesBeforeVerify = throwAfterProfilesBeforeVerify;
    }

    internal static InternetFirewallApplierTestInjections None { get; } = new();
}