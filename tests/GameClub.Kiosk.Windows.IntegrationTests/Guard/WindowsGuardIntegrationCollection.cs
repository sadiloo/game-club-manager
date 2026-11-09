#if NET48
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsGuardIntegrationCollection
{
    public const string Name = "windows-guard-integration";
}
#endif
