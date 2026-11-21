using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFreeUsePresenceTests
{
    [Fact]
    public void open_row_becomes_no_contact_after_three_minutes_without_contact()
    {
        var start = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var lastContact = start.AddMinutes(1);
        var now = lastContact.AddMinutes(3).AddSeconds(1);
        Assert.True(KioskFreeUsePresence.IsNoContact(lastContact, now, isOpen: true));
    }

    [Fact]
    public void duration_frozen_at_last_contact_for_no_contact_open_row()
    {
        var start = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var lastContact = start.AddMinutes(5);
        var now = lastContact.AddMinutes(10);
        var end = KioskFreeUsePresence.EffectiveDurationEnd(null, lastContact, now, noContact: true);
        Assert.Equal(lastContact, end);
        Assert.Equal(300, (end - start).TotalSeconds);
    }

    [Fact]
    public void open_row_at_179_seconds_since_contact_is_not_no_contact()
    {
        var last = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = last.AddSeconds(179);
        Assert.False(KioskFreeUsePresence.IsNoContact(last, now, isOpen: true));
    }

    [Fact]
    public void open_row_at_180_seconds_since_contact_is_still_reachable()
    {
        var last = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = last.AddSeconds(180);
        Assert.False(KioskFreeUsePresence.IsNoContact(last, now, isOpen: true));
    }

    [Fact]
    public void open_row_at_181_seconds_since_contact_is_no_contact()
    {
        var last = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = last.AddSeconds(181);
        Assert.True(KioskFreeUsePresence.IsNoContact(last, now, isOpen: true));
    }

    [Fact]
    public void closed_row_is_never_no_contact()
    {
        var lastContact = DateTime.UtcNow.AddHours(-1);
        Assert.False(KioskFreeUsePresence.IsNoContact(lastContact, DateTime.UtcNow, isOpen: false));
    }
}