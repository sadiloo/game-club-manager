using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskLoginErrorDisplayStateTests
{
    [Fact]
    public void login_error_message_persists_until_user_input_clears_it()
    {
        var state = new KioskLoginErrorDisplayState();
        var lockState = KioskStationStatus.FormatLocalLockScreenState(
            "PC-1",
            leaseExpired: false,
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            lastErrorDetail: null);
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.InvalidLogin);

        state.SetLoginError(error);
        var tick1 = KioskStatusPresentationBuilder.Build(lockState, error, null).ToCombinedText();
        var tick2 = KioskStatusPresentationBuilder.Build(lockState, error, null).ToCombinedText();

        Assert.Equal(tick1, tick2);
        Assert.Contains(error, tick1, StringComparison.Ordinal);

        state.OnLoginUserInput();
        Assert.Null(state.ActiveLoginError);
        var baseOnly = KioskStatusPresentationBuilder.Build(lockState, null, null).ToCombinedText();
        Assert.Equal(baseOnly, state.ApplyToLockLine(baseOnly));
    }

    [Fact]
    public void unpaired_direct_path_shows_login_error_without_lock_line_prefix()
    {
        var state = new KioskLoginErrorDisplayState();
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.PairBefore);
        state.SetLoginError(error);
        Assert.Equal(error, state.ApplyUnpairedDirect());
        var presentation = KioskStatusPresentationBuilder.Build(KioskStatusLineState.Unpaired(), error, null);
        Assert.Equal(error, presentation.ToCombinedText());
    }
}