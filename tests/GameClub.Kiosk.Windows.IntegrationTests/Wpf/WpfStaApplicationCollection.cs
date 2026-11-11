#if NET48
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

[CollectionDefinition("windows-wpf-sta", DisableParallelization = true)]
public sealed class WpfStaApplicationCollection : ICollectionFixture<WpfStaApplicationFixture>
{
}
#endif
