namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Login error text held until the user edits login fields or presses Login (display only).</summary>
public sealed class KioskLoginErrorDisplayState
{
    public string? ActiveLoginError { get; private set; }

    public void SetLoginError(string? displayText) => ActiveLoginError = displayText;

    public void OnLoginUserInput() => ActiveLoginError = null;

    public string ApplyToLockLine(string lockLineBase)
    {
        var loginError = ActiveLoginError;
        if (string.IsNullOrEmpty(loginError))
        {
            return lockLineBase;
        }

        return KioskLoginUserMessages.AppendToLockLine(lockLineBase, loginError!);
    }

    public string ApplyUnpairedDirect()
    {
        var loginError = ActiveLoginError;
        return string.IsNullOrEmpty(loginError) ? string.Empty : loginError!;
    }
}