namespace GameClub.Kiosk.Core;

/// <summary>
/// Alt+F4 and the window close button must not exit the shell.
/// Only the guard (maintenance) or an explicit allow flag may close it.
/// </summary>
public static class ShellClosePolicy
{
    public static bool CancelClose(bool allowClose) => !allowClose;

    public static bool SwallowAltF4(bool isSystemKeyF4, bool altDown) => isSystemKeyF4 && altDown;
}
