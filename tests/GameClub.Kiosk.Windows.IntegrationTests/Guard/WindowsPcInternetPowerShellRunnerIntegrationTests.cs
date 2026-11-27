#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GameClub.Kiosk.Guard.Internet;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Collection(PcInternetFirewallWindowsIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetPowerShellRunnerIntegrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task internet_powershell_runner_times_out_hung_child_and_kills_process_tree()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            FirewallTestConsoleDiagnostics.FailAssertion("Windows-only Guard internet integration.");
        }

        using var pipeDiag = InternetPipeOwnerDiagnostics.BeginTestScope(nameof(internet_powershell_runner_times_out_hung_child_and_kills_process_tree));
        var pidFile = Path.Combine(Path.GetTempPath(), $"gcm-ps-hang-{Guid.NewGuid():N}.txt");
        try
        {
            await Task.Run(() =>
            {
                var script = $@"
Set-Content -Path '{pidFile.Replace("'", "''")}' -Value $PID
Start-Sleep -Seconds 600
";
                var budgetMs = 4_000;
                var sw = Stopwatch.StartNew();
                int childPid = 0;
                var runTask = Task.Run(() => InternetPowerShellRunner.Run(script, budgetMs, stepName: "integration_hang_stub"));
                var pidDeadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < pidDeadline && childPid == 0)
                {
                    if (File.Exists(pidFile)
                        && int.TryParse(File.ReadAllText(pidFile).Trim(), out childPid))
                    {
                        break;
                    }

                    System.Threading.Thread.Sleep(50);
                }

                Assert.True(childPid > 0, "hung child should have written pid before timeout");
                ProcessTreeDiagnostics.LogProcessForest($"before_kill_child_root pid={childPid}");
                ProcessTreeDiagnostics.LogPidExitState("before_kill_target", childPid, waitMs: 1);

                var result = runTask.GetAwaiter().GetResult();
                sw.Stop();

                Assert.Equal("apply_failed:powershell_timeout", result);
                Assert.True(sw.ElapsedMilliseconds < budgetMs + 6_000, $"elapsed={sw.ElapsedMilliseconds}");

                ProcessTreeDiagnostics.LogProcessForest($"after_kill_child_root pid={childPid}");
                var killTargets = InternetPowerShellProcessTree.LastKillTreeTargets;
                Assert.NotEmpty(killTargets);
                Assert.Contains(childPid, killTargets);
                foreach (var pid in killTargets)
                {
                    try
                    {
                        using var stillAlive = Process.GetProcessById(pid);
                        FirewallTestConsoleDiagnostics.FailAssertion(
                            $"pid {pid} should be gone when Run returns after timeout kill");
                    }
                    catch (ArgumentException)
                    {
                        /* expected */
                    }
                }
            });
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