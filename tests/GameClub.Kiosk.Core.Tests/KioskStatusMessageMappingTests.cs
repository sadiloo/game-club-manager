using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskStatusMessageMappingTests
{
    [Fact]
    public void every_status_key_resolves_to_nonempty_text()
    {
        foreach (var key in KioskStatusMessages.MappedKeys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var text = KioskStatusMessages.PreviewText(key);
            Assert.False(string.IsNullOrWhiteSpace(text), $"empty preview for key {key}");
        }

    }
}