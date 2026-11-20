using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationOriginTests
{
    [Theory]
    [InlineData("http://127.0.0.1:50001/operator/")]
    [InlineData("http://127.0.0.1:50001/")]
    [InlineData("http://127.0.0.1:5000.evil.com/path")]
    [InlineData("http://127.0.0.1:5000@evil.com/path")]
    [InlineData("https://127.0.0.1:5000/operator/")]
    [InlineData("https://127.0.0.1:5000/")]
    public void panel_navigation_origin_rejects_spoofed_or_mismatched_uris(string uri)
    {
        Assert.False(PanelNavigationOrigin.IsAllowedPanelNavigationUri(uri));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/operator/")]
    [InlineData("http://127.0.0.1:5000/operator/index.html")]
    [InlineData("http://127.0.0.1:5000/operator/foo/bar")]
    [InlineData("http://127.0.0.1:5000/")]
    public void panel_navigation_origin_allows_panel_url_and_subpaths(string uri)
    {
        Assert.True(PanelNavigationOrigin.IsAllowedPanelNavigationUri(uri));
    }
}