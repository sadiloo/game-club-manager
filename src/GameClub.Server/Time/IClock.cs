namespace GameClub.Server.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class MutableClock : IClock
{
    private DateTimeOffset _utcNow;

    public MutableClock(DateTimeOffset utcNow) => _utcNow = utcNow;

    public DateTimeOffset UtcNow => _utcNow;

    public void SetUtc(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void SetTehran(int year, int month, int day, int hour, int minute, int second = 0)
    {
        var tehran = GameClub.Billing.TehranTime.At(year, month, day, hour, minute, second);
        _utcNow = tehran.ToUniversalTime();
    }
}
