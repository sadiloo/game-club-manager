using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelLoopbackNavigationDispatchPolicyTests
{
    [Fact]
    public void loopback_panel_loads_require_core_webview2_navigate_not_wpf_source_only()
    {
        Assert.Equal("CoreWebView2.Navigate", PanelLoopbackNavigationDispatchPolicy.RequiredApi);
    }
}