#if NET48
using System;
using System.Net;
using GameClub.Shell.Shell;

namespace GameClub.Shell.Windows.IntegrationTests.Seams;

internal sealed class StubAlignedHttpProbeSeam : IHttpProbeSeam
{
    private readonly string _probeUrl;

    public StubAlignedHttpProbeSeam(string probeUrl) => _probeUrl = probeUrl;

    public bool TryGetOperatorOk(out int statusCode)
    {
        statusCode = 0;
        try
        {
            var request = WebRequest.Create(_probeUrl);
            request.Timeout = 3000;
            using var response = (HttpWebResponse)request.GetResponse();
            statusCode = (int)response.StatusCode;
            return statusCode is >= 200 and < 300;
        }
        catch
        {
            return false;
        }
    }
}
#endif