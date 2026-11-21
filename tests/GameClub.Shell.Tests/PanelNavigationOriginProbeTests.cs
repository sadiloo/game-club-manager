using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationOriginProbeTests
{
    [Theory]
    [InlineData("http://127.0.0.1:50001/operator/", false)]
    [InlineData("http://127.0.0.1:5000.evil.com/operator/", false)]
    [InlineData("http://127.0.0.1:5000@evil.com/operator/", false)]
    [InlineData("http://127.0.0.1/operator/", false)]
    [InlineData("http://127.0.0.1:80/operator/", false)]
    [InlineData("https://127.0.0.1:5000/operator/", false)]
    [InlineData("http://localhost:5000/operator/", false)]
    [InlineData("http://[::1]:5000/operator/", false)]
    [InlineData("http://127.0.0.2:5000/operator/", false)]
    [InlineData("http://evil.com/?u=http://127.0.0.1:5000/", false)]
    [InlineData("ws://127.0.0.1:5000/hubs/operator", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/html,<p>x", false)]
    [InlineData("http://127.0.0.1:5000\\@evil.com/", false)]
    [InlineData("127.0.0.1:5000/operator/", false)]
    [InlineData("http://127.0.0.1:5000/operator/", true)]
    [InlineData("HTTP://127.0.0.1:5000/OPERATOR/", true)]
    [InlineData("http://127.0.0.1:05000/operator/", true)]
    [InlineData("http://user:pw@127.0.0.1:5000/operator/", false)]
    [InlineData("http://127.0.0.1.:5000/operator/", false)]
    public void probe_origin(string uri, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, PanelNavigationOrigin.IsAllowedPanelNavigationUri(uri));
    }
}