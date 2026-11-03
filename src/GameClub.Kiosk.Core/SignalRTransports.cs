namespace GameClub.Kiosk.Core;

/// <summary>
/// Bit values match ASP.NET Core <c>HttpTransportType</c> so the shell can map them without
/// taking a dependency on the SignalR package inside this library.
/// Windows 7 cannot use WebSockets. Server-Sent Events and long polling stay enabled everywhere
/// so the client still has a fallback when a WebSocket upgrade fails.
/// </summary>
public static class SignalRTransports
{
    public const int WebSockets = 1;
    public const int ServerSentEvents = 2;
    public const int LongPolling = 4;

    public static int Select(bool clientWebSocketSupported)
    {
        const int fallback = ServerSentEvents | LongPolling;
        return clientWebSocketSupported ? WebSockets | fallback : fallback;
    }

    public static bool UsesWebSockets(int mask) => (mask & WebSockets) != 0;

    public static bool UsesServerSentEvents(int mask) => (mask & ServerSentEvents) != 0;

    public static bool UsesLongPolling(int mask) => (mask & LongPolling) != 0;
}
