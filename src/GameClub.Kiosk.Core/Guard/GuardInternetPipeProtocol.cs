namespace GameClub.Kiosk.Core.Guard;

/// <summary>NDJSON line protocol for the Guard internet named pipe (one JSON object per line).</summary>
public static class GuardInternetPipeProtocol
{
    public const int MaxLineBytes = 64 * 1024;

    public static string FrameRequest(string json) => json + "\n";
}