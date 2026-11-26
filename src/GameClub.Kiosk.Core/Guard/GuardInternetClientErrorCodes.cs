using System;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Stable Guard pipe client error codes for kiosk heartbeat reports (never raw .NET messages).</summary>
public static class GuardInternetClientErrorCodes
{
    public const string ConnectTimeout = "pipe_connect_timeout";
    public const string IoTimeout = "pipe_io_timeout";
    public const string PipeError = "pipe_error";

    public static string MapException(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return MapTimeoutMessage(ex.Message);
        }

        return PipeError;
    }

    private static string MapTimeoutMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return IoTimeout;
        }

        if (message == ConnectTimeout)
        {
            return ConnectTimeout;
        }

        if (message == IoTimeout || message is "pipe_read_timeout" or "pipe_write_timeout")
        {
            return IoTimeout;
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectTimeout;
        }

        return IoTimeout;
    }
}