namespace GameClub.Kiosk.Guard.Internet;

public interface IInternetFirewallApplier
{
    string Apply(InternetGuardConfig config, bool on);

    string RunResetScript();
}