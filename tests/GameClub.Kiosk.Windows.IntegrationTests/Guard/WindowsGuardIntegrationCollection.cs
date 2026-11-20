#if NET48
using System;
using System.Diagnostics;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

public sealed class WindowsGuardIntegrationFixture : IDisposable
{
    public WindowsGuardIntegrationFixture()
    {
        InternetPipeOwnerDiagnostics.WriteCollectionPhase("fixture_begin");
        InternetPipeOwnerDiagnostics.AssertNoStrayGuardOrPipeOwnerAtCollectionStart();
    }

    public void Dispose()
    {
        InternetPipeOwnerDiagnostics.WriteCollectionPhase("fixture_end");
        foreach (var processName in new[] { "GameClub.Kiosk", "GameClub.Kiosk.Guard" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    GuardProcessLifecycle.StopGuardProcessTree(process, TimeSpan.FromSeconds(20));
                }
                catch
                {
                    // best-effort cleanup so GHA runner does not accumulate guard/shell children
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        InternetPipeOwnerDiagnostics.WriteCollectionPhase("fixture_end_after_cleanup");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsGuardIntegrationCollection : ICollectionFixture<WindowsGuardIntegrationFixture>
{
    public const string Name = "windows-guard-integration";
}
#endif
