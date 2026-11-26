#if NET48
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>
/// Serializes all host-mutating PC internet firewall and Guard pipe integration tests
/// (one process, one Windows firewall state). xUnit still parallelizes the default
/// (uncollected) test bucket with named collections unless every mutating class joins here.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PcInternetFirewallWindowsIntegrationCollection : ICollectionFixture<WindowsGuardIntegrationFixture>
{
    public const string Name = "pc-internet-firewall-windows";
}
#endif