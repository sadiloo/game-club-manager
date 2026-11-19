#if NET48
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[CollectionDefinition("windows-shell-sta")]
public sealed class WpfStaApplicationCollection : ICollectionFixture<WpfStaApplicationFixture>
{
}
#endif