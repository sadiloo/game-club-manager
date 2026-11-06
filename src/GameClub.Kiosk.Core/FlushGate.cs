namespace GameClub.Kiosk.Core;

/// <summary>
/// One in-flight server sync. A second caller is told to leave the first one alone
/// so SQLite and Logout are never entered from two threads at once.
/// </summary>
public sealed class FlushGate
{
    private int _held;

    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Exit() => Interlocked.Exchange(ref _held, 0);

    public bool IsHeld => Volatile.Read(ref _held) == 1;
}
