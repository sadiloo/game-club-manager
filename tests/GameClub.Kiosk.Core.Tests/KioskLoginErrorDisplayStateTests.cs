using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskLoginErrorDisplayStateTests
{
    [Fact]
    public void login_error_message_persists_until_user_input_clears_it()
    {
        var state = new KioskLoginErrorDisplayState();
        const string baseLine = "Locked — PC-1 · connected";
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.InvalidLogin);

        state.SetLoginError(error);
        var tick1 = state.ApplyToLockLine(baseLine);
        var tick2 = state.ApplyToLockLine(baseLine);

        Assert.Equal(tick1, tick2);
        Assert.Contains(error, tick1, StringComparison.Ordinal);

        state.OnLoginUserInput();
        Assert.Null(state.ActiveLoginError);
        Assert.Equal(baseLine, state.ApplyToLockLine(baseLine));
    }

    [Fact]
    public void unpaired_direct_path_shows_login_error_without_lock_line_prefix()
    {
        var state = new KioskLoginErrorDisplayState();
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.PairBefore);
        state.SetLoginError(error);
        Assert.Equal(error, state.ApplyUnpairedDirect());
    }
}