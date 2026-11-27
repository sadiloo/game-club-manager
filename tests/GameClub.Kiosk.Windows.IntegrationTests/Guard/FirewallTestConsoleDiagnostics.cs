#if NET48
#nullable disable
using System;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class FirewallTestConsoleDiagnostics
{
    internal static void LogPowerShellResult(string phase, string label, WindowsIntegrationRunPowerShell.PowerShellRunResult result, string script)
    {
        Console.WriteLine($"FW_TEST_DIAG phase={phase} label={label} exitCode={result.ExitCode}");
        Console.WriteLine($"FW_TEST_DIAG stdout={result.StdOut}");
        Console.WriteLine($"FW_TEST_DIAG stderr={result.StdErr}");
        Console.WriteLine($"FW_TEST_DIAG script={script}");
    }

    internal static void Fail(
        string phase,
        string summary,
        WindowsIntegrationRunPowerShell.PowerShellRunResult? tamper = null,
        string tamperScript = null,
        WindowsIntegrationRunPowerShell.PowerShellRunResult? preState = null,
        string preStateScript = null)
    {
        Console.WriteLine($"FW_TEST_FAIL phase={phase} summary={summary}");
        if (preState is not null)
        {
            LogPowerShellResult(phase, "pre_tamper_state", preState.Value, preStateScript ?? "(pre-state script)");
        }

        if (tamper is not null)
        {
            LogPowerShellResult(phase, "tamper", tamper.Value, tamperScript ?? "(tamper script)");
        }

        Assert.Fail(summary);
    }

    internal static void FailAssertion(string message)
    {
        Console.WriteLine($"FW_TEST_FAIL message={message}");
        Assert.Fail(message);
    }

    internal static void FailAssertion(string phase, string message)
    {
        Console.WriteLine($"FW_TEST_FAIL phase={phase} message={message}");
        Assert.Fail(message);
    }
}
#endif