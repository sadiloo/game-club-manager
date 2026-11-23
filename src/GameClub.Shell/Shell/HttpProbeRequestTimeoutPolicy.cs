using System.Net;

namespace GameClub.Shell.Shell;

internal static class HttpProbeRequestTimeoutPolicy
{
    public const int StandardMilliseconds = 3000;

    public const int HealthyProbeMilliseconds = 3000;

    public static void Apply(WebRequest request, int timeoutMilliseconds)
    {
        request.Timeout = timeoutMilliseconds;
        if (request is HttpWebRequest http)
        {
            http.ReadWriteTimeout = timeoutMilliseconds;
        }
    }
}