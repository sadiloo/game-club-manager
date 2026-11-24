using System.Net;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class HttpProbeRequestTimeoutPolicyTests
{
    [Fact]
    public void healthy_probe_uses_at_most_3000_ms_on_the_http_request()
    {
        Assert.Equal(3000, HttpProbeRequestTimeoutPolicy.HealthyProbeMilliseconds);
        Assert.Equal(
            HttpProbeRequestTimeoutPolicy.HealthyProbeMilliseconds,
            (int)HealthyRunningPanelProbePolicy.PerProbeTimeout.TotalMilliseconds);

#pragma warning disable SYSLIB0014
        var request = WebRequest.Create("http://127.0.0.1:5000/operator/");
#pragma warning restore SYSLIB0014
        HttpProbeRequestTimeoutPolicy.Apply(request, HttpProbeRequestTimeoutPolicy.HealthyProbeMilliseconds);
        Assert.Equal(3000, request.Timeout);
        var http = Assert.IsType<HttpWebRequest>(request);
        Assert.Equal(3000, http.ReadWriteTimeout);
    }

    [Fact]
    public void standard_probe_keeps_3000_ms_default()
    {
        Assert.Equal(3000, HttpProbeRequestTimeoutPolicy.StandardMilliseconds);
    }
}