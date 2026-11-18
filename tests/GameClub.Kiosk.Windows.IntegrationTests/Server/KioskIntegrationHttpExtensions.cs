#if NET10_0_OR_GREATER
using System.Net.Http;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class KioskIntegrationHttpExtensions
{
    public static async Task EnsureSuccessStatusCodeWithBodyAsync(this HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"{operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
    }
}
#endif