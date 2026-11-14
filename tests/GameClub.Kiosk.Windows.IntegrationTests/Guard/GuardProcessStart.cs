#if NET48
#nullable disable
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class GuardProcessStart
{
    internal static Process StartGuard(string kioskExe = null, string guardExe = null)
    {
        guardExe ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.Guard.exe");
        Assert.True(File.Exists(guardExe), "GameClub.Kiosk.Guard.exe not found beside test output.");
        kioskExe ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameClub.Kiosk.exe");

        var psi = new ProcessStartInfo(guardExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(guardExe)!,
        };
        CopyParentEnvironment(psi);
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("CHECKPOINT_KIOSK_SID must be set on windows-2022 kiosk integration.");
        }

        psi.EnvironmentVariables["CHECKPOINT_KIOSK_SID"] = sid;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_USER"] =
            Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? Environment.UserName;
        psi.EnvironmentVariables["CHECKPOINT_KIOSK_EXE"] = kioskExe;
        var guard = Process.Start(psi);
        Assert.NotNull(guard);
        return guard;
    }

    private static void CopyParentEnvironment(ProcessStartInfo psi)
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key as string;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            psi.EnvironmentVariables[key] = entry.Value?.ToString() ?? string.Empty;
        }
    }
}
#endif
