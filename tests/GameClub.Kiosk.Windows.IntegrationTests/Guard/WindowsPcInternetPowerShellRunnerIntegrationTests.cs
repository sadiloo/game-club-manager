#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using GameClub.Kiosk.Guard.Internet;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetPowerShellRunnerIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public void internet_powershell_runner_times_out_hung_child_and_kills_process_tree()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Fail("Windows-only Guard internet integration.");
        }

        var pidFile = Path.Combine(Path.GetTempPath(), $"gcm-ps-hang-{Guid.NewGuid():N}.txt");
        try
        {
            var script = $@"
Set-Content -Path '{pidFile.Replace("'", "''")}' -Value $PID
Start-Sleep -Seconds 600
";
            var budgetMs = 4_000;
            var sw = Stopwatch.StartNew();
            var result = InternetPowerShellRunner.Run(script, budgetMs, stepName: "integration_hang_stub");
            sw.Stop();

            Assert.Equal("apply_failed:powershell_timeout", result);
            Assert.True(sw.ElapsedMilliseconds < budgetMs + 6_000, $"elapsed={sw.ElapsedMilliseconds}");

            Assert.True(File.Exists(pidFile), "hung child should have written pid before timeout");
            var pidText = File.ReadAllText(pidFile).Trim();
            Assert.True(int.TryParse(pidText, out var childPid), $"pid file: {pidText}");

            try
            {
                using var stillAlive = Process.GetProcessById(childPid);
                Assert.Fail($"child pid {childPid} should be gone after timeout kill");
            }
            catch (ArgumentException)
            {
                /* expected */
            }
        }
        finally
        {
            try
            {
                if (File.Exists(pidFile))
                {
                    File.Delete(pidFile);
                }
            }
            catch
            {
            }
        }
    }
}
#endif