namespace GameClub.Shell.Shell;

internal static class PanelNavigationHttpOutcome
{
    public static bool IsSuccessfulPanelHttpNavigation(bool isSuccess, bool webErrorIsUnknown, int httpStatusCode)
    {
        if (!isSuccess)
        {
            return false;
        }

        if (!webErrorIsUnknown)
        {
            return false;
        }

        return httpStatusCode >= 200 && httpStatusCode < 300;
    }

    /// <summary>
    /// While panel load is failing, only a confirmed 2xx clears failing state (http 0 is not recovery).
    /// </summary>
    public static bool IsConfirmedPanelHttpRecovery(bool isSuccess, bool webErrorIsUnknown, int httpStatusCode)
    {
        if (!isSuccess)
        {
            return false;
        }

        if (!webErrorIsUnknown)
        {
            return false;
        }

        return httpStatusCode >= 200 && httpStatusCode < 300;
    }

    public static bool ShouldRecoverFromPanelNavigation(
        bool runningPanelLoadFailing,
        bool isSuccess,
        bool webErrorIsUnknown,
        int httpStatusCode)
    {
        return runningPanelLoadFailing
            ? IsConfirmedPanelHttpRecovery(isSuccess, webErrorIsUnknown, httpStatusCode)
            : IsSuccessfulPanelHttpNavigation(isSuccess, webErrorIsUnknown, httpStatusCode);
    }
}