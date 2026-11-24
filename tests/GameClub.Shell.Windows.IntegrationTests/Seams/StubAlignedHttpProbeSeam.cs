#if NET48
using System;
using System.Net;
using GameClub.Shell.Shell;

namespace GameClub.Shell.Windows.IntegrationTests.Seams;

internal sealed class StubAlignedHttpProbeSeam : IHttpProbeSeam
{
    private readonly string _probeUrl;

    public StubAlignedHttpProbeSeam(string probeUrl) => _probeUrl = probeUrl;

    /// <summary>
    /// When set, overrides the caller timeout for <see cref="TryGetPanelOk"/> (held operator recovery probes in integration tests).
    /// </summary>
    public int? PanelOkTimeoutOverrideMilliseconds { get; set; }

    public bool TryGetOperatorOk(out int statusCode) =>
        TryGetOperatorOk(out statusCode, timeoutMilliseconds: 3000);

    public bool TryGetPanelOk(out int statusCode) =>
        TryGetPanelOk(out statusCode, timeoutMilliseconds: 3000);

    public bool TryGetOperatorOk(out int statusCode, int timeoutMilliseconds)
    {
        statusCode = 0;
        try
        {
            var request = WebRequest.Create(_probeUrl);
            HttpProbeRequestTimeoutPolicy.Apply(request, timeoutMilliseconds);
            using var response = (HttpWebResponse)request.GetResponse();
            statusCode = (int)response.StatusCode;
            return statusCode is >= 200 and < 300;
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
        {
            statusCode = (int)errorResponse.StatusCode;
            return false;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetPanelOk(out int statusCode, int timeoutMilliseconds)
    {
        statusCode = 0;
        try
        {
            var request = WebRequest.Create(ShellSeams.ResolvePanelUrl());
            var effectiveTimeout = PanelOkTimeoutOverrideMilliseconds ?? timeoutMilliseconds;
            HttpProbeRequestTimeoutPolicy.Apply(request, effectiveTimeout);
            using var response = (HttpWebResponse)request.GetResponse();
            statusCode = (int)response.StatusCode;
            return statusCode is >= 200 and < 300;
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse errorResponse)
        {
            statusCode = (int)errorResponse.StatusCode;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
#endif