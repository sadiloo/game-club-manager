using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace GameClub.Kiosk.Core.Runtime;

public static class KioskServerTransportErrors
{
    public const string UnreachableDetailPrefix = "unreachable:";

    public static string MessageKey => KioskStatusMessages.Keys.ServerUnreachable;

    public static bool IsUnreachable(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException
                || cur is WebException { Status: WebExceptionStatus.ConnectFailure or WebExceptionStatus.NameResolutionFailure or WebExceptionStatus.Timeout }
                || cur is TaskCanceledException canceled && !canceled.CancellationToken.IsCancellationRequested)
            {
                return true;
            }

#if NET5_0_OR_GREATER
            if (cur is HttpRequestException { StatusCode: not null })
            {
                return false;
            }
#endif
            if (cur is HttpRequestException)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsUnreachableDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return detail!.StartsWith(UnreachableDetailPrefix, StringComparison.Ordinal);
    }

    public static string FormatDetail(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return IsUnreachable(ex) ? $"{UnreachableDetailPrefix}{message}" : message;
    }

    public static string? TryGetMessageKey(Exception ex)
        => IsUnreachable(ex) ? MessageKey : null;

    public static string DisplayText(string? messageKey)
        => messageKey is null
            ? string.Empty
            : string.Equals(messageKey, MessageKey, StringComparison.Ordinal)
                ? KioskStatusMessages.DisplayText(MessageKey)
                : KioskLoginUserMessages.DisplayText(messageKey);
}