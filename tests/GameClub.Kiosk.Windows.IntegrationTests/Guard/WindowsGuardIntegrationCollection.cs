#if NET48
using System.Diagnostics;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

public sealed class WindowsGuardIntegrationFixture : IDisposable
{
    public void Dispose()
    {
        foreach (var processName in new[] { "GameClub.Kiosk", "GameClub.Kiosk.Guard" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
                catch
                {
                    // best-effort cleanup so GHA runner does not accumulate guard/shell children
                }
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsGuardIntegrationCollection : ICollectionFixture<WindowsGuardIntegrationFixture>
{
    public const string Name = "windows-guard-integration";
}
#endif
