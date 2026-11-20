using System;
using System.Text.RegularExpressions;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetPowerShellStderr
{
    internal static string FormatFailureDetail(string stderr, string stdout)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "exit_nonzero";
        }

        if (detail.StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            return TryDecodeClixmlMessage(detail) ?? "powershell_error_clixml";
        }

        return detail;
    }

    private static string? TryDecodeClixmlMessage(string clixml)
    {
        var error = Regex.Match(clixml, @"<S S=""Error"">([^<]*)</S>");
        if (error.Success && !string.IsNullOrWhiteSpace(error.Groups[1].Value))
        {
            return error.Groups[1].Value.Trim();
        }

        var toString = Regex.Match(clixml, @"<ToString>([^<]*)</ToString>");
        if (toString.Success && !string.IsNullOrWhiteSpace(toString.Groups[1].Value))
        {
            return toString.Groups[1].Value.Trim();
        }

        return null;
    }
}