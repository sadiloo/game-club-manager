using GameClub.Server.Time;

namespace GameClub.Server.Tests;

public sealed class TestMutableClock : IClock
{
    private DateTimeOffset _utcNow;

    public TestMutableClock(DateTimeOffset utcNow) => _utcNow = utcNow;

    public DateTimeOffset UtcNow => _utcNow;

    public void SetUtc(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void SetTehran(int year, int month, int day, int hour, int minute, int second = 0)
    {
        var tehran = GameClub.Billing.TehranTime.At(year, month, day, hour, minute, second);
        _utcNow = tehran.ToUniversalTime();
    }
}