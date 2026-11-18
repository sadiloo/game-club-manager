#if NET10_0_OR_GREATER
using GameClub.Server.Tests;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Invoked by windows CI scripts via --filter Category=LiveDbResetHarness.</summary>
[Trait("Category", "LiveDbResetHarness")]
public sealed class LiveDatabaseResetHarnessTests
{
    [Fact]
    public async Task harness_live_database_reset_only()
    {
        await LiveDatabaseMigration.EnsureMigratedAsync();
        await LiveDatabaseReset.ResetAsync();
    }
}
#endif