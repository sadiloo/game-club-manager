namespace GameClub.Shell.Shell;

/// <summary>
/// Loopback operator panel loads must call CoreWebView2.Navigate only. Do not assign the
/// WPF WebView2.Source afterward — that setter starts a second navigation. Assigning Source
/// to the URI that is already current is also a no-op and does not raise NavigationStarting
/// (see R27 CI: offline data-nav leaves Source at panel URL).
/// </summary>
internal static class PanelLoopbackNavigationDispatchPolicy
{
    public const string RequiredApi = "CoreWebView2.Navigate";
}