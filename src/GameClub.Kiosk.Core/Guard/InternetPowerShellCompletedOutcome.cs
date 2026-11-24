using System;
using System.Text.RegularExpressions;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Classifies a finished PowerShell child (exit code + streams); stderr alone is not failure when exit is 0.</summary>
public static class InternetPowerShellCompletedOutcome
{
    public static string ResolveOutput(int exitCode, string stdout, string stderr)
    {
        stdout ??= "";
        stderr ??= "";
        if (exitCode != 0)
        {
            var detail = FormatFailureDetail(stderr, stdout);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"exit_{exitCode}";
            }

            return detail.StartsWith("apply_failed", StringComparison.Ordinal)
                ? detail
                : $"apply_failed:powershell_error:{detail}";
        }

        var tail = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
        return string.IsNullOrEmpty(tail) ? "" : tail!;
    }

    public static string FormatFailureDetail(string stderr, string stdout)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "";
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