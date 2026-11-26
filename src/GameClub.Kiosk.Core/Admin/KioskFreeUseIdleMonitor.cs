namespace GameClub.Kiosk.Core.Admin;

/// <summary>Idle timer for open free use with v2.7 warning window min(60s, N×30s).</summary>
public sealed class KioskFreeUseIdleMonitor
{
    private DateTime _lastInputUtc;
    private int _idleMinutes = 5;
    private DateTime? _warningStartedUtc;
    private int _activeWarnSeconds;

    public KioskFreeUseIdleMonitor(DateTime initialUtc) => _lastInputUtc = initialUtc;

    public static int WarningSecondsForIdleMinutes(int idleMinutes)
        => Math.Min(60, idleMinutes * 30);

    public void SetIdleMinutes(int minutes)
    {
        if (minutes < 1)
        {
            minutes = 1;
        }

        if (minutes > KioskOpStrings.MaxFreeUseIdleMinutes)
        {
            minutes = KioskOpStrings.MaxFreeUseIdleMinutes;
        }

        _idleMinutes = minutes;
    }

    public void RecordInput(DateTime utc)
    {
        _lastInputUtc = utc;
        _warningStartedUtc = null;
        _activeWarnSeconds = 0;
    }

    public int IdleMinutes => _idleMinutes;

    public bool ShouldAutoLock(DateTime utc)
    {
        var lockAt = _lastInputUtc.AddMinutes(_idleMinutes);
        var warnSeconds = WarningSecondsForIdleMinutes(_idleMinutes);

        if (utc < lockAt)
        {
            return false;
        }

        if (_warningStartedUtc is null)
        {
            _warningStartedUtc = utc;
            _activeWarnSeconds = warnSeconds;
            return false;
        }

        var warnedFor = utc - _warningStartedUtc.Value;
        return warnedFor >= TimeSpan.FromSeconds(_activeWarnSeconds);
    }

    public bool TryGetWarningCountdown(DateTime utc, out int secondsRemaining)
    {
        secondsRemaining = 0;
        var lockAt = _lastInputUtc.AddMinutes(_idleMinutes);
        var warnSeconds = WarningSecondsForIdleMinutes(_idleMinutes);
        var warnAt = lockAt.AddSeconds(-warnSeconds);
        var idle = utc - _lastInputUtc;

        if (utc >= lockAt)
        {
            if (_warningStartedUtc is null)
            {
                return false;
            }

            var left = TimeSpan.FromSeconds(_activeWarnSeconds) - (utc - _warningStartedUtc.Value);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            secondsRemaining = (int)Math.Ceiling(left.TotalSeconds);
            return true;
        }

        if (utc < warnAt)
        {
            return false;
        }

        secondsRemaining = (int)Math.Ceiling((lockAt - utc).TotalSeconds);
        return secondsRemaining > 0;
    }

    public string FormatWarningText(int secondsRemaining)
        => KioskOpStrings.FreeBannerWarn.Replace("{s}", secondsRemaining.ToString());
}