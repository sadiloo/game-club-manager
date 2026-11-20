using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskStatusMessageMappingTests
{
    [Fact]
    public void every_status_key_resolves_to_nonempty_text()
    {
        foreach (var key in KioskStatusMessages.MappedKeys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var text = KioskStatusMessages.DisplayText(key);
            Assert.False(string.IsNullOrWhiteSpace(text), $"empty display for key {key}");
        }
    }

    [Fact]
    public void station_connection_preview_composes_persian_join()
    {
        var state = new KioskStatusLineState(
            KioskStatusRootKind.StationWithConnection,
            "Q-0001",
            KioskStatusConnectionKind.NotConnected,
            null);
        var text = KioskStatusPresentationBuilder.Build(state, null, null).ToCombinedText();
        Assert.Contains(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.ConnNotConnected), text, StringComparison.Ordinal);
        Assert.Contains("Q-0001", text, StringComparison.Ordinal);
        Assert.Contains(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Join), text, StringComparison.Ordinal);
    }
}