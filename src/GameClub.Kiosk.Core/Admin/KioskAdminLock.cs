namespace GameClub.Kiosk.Core.Admin;

public enum KioskAdminRole
{
    Manager,
    Operator,
}

public sealed class KioskAdminLockState
{
    public KioskAdminRole Role { get; init; }

    public int WrongCount { get; set; }

    public DateTime? LockedUntilUtc { get; set; }
}

public sealed class KioskAdminLock
{
    public const int WrongLimit = 2;
    public const int LockMinutes = 15;

    private readonly Dictionary<KioskAdminRole, KioskAdminLockState> _roles = new();

    public bool IsLocked(KioskAdminRole role, DateTime utcNow, out DateTime? lockedUntilUtc)
    {
        var state = GetState(role);
        lockedUntilUtc = state.LockedUntilUtc;
        if (lockedUntilUtc is null)
        {
            return false;
        }

        if (utcNow >= lockedUntilUtc.Value)
        {
            state.LockedUntilUtc = null;
            state.WrongCount = 0;
            lockedUntilUtc = null;
            return false;
        }

        return true;
    }

    public void RegisterWrong(KioskAdminRole role, DateTime utcNow)
    {
        var state = GetState(role);
        if (state.LockedUntilUtc is not null && utcNow < state.LockedUntilUtc.Value)
        {
            return;
        }

        state.WrongCount++;
        if (state.WrongCount >= WrongLimit)
        {
            state.LockedUntilUtc = utcNow.AddMinutes(LockMinutes);
        }
    }

    public void RegisterSuccess(KioskAdminRole role)
    {
        var state = GetState(role);
        state.WrongCount = 0;
        state.LockedUntilUtc = null;
    }

    public void ClearOperatorLock()
    {
        var state = GetState(KioskAdminRole.Operator);
        state.WrongCount = 0;
        state.LockedUntilUtc = null;
    }

    public IReadOnlyDictionary<KioskAdminRole, KioskAdminLockState> Snapshot() =>
        _roles.ToDictionary(kv => kv.Key, kv => new KioskAdminLockState
        {
            Role = kv.Key,
            WrongCount = kv.Value.WrongCount,
            LockedUntilUtc = kv.Value.LockedUntilUtc,
        });

    public void Restore(IEnumerable<KioskAdminLockState> states)
    {
        _roles.Clear();
        foreach (var state in states)
        {
            _roles[state.Role] = new KioskAdminLockState
            {
                Role = state.Role,
                WrongCount = state.WrongCount,
                LockedUntilUtc = state.LockedUntilUtc,
            };
        }
    }

    public static int RemainingSeconds(DateTime utcNow, DateTime lockedUntilUtc)
        => Math.Max(0, (int)Math.Ceiling((lockedUntilUtc - utcNow).TotalSeconds));

    private KioskAdminLockState GetState(KioskAdminRole role)
    {
        if (!_roles.TryGetValue(role, out var state))
        {
            state = new KioskAdminLockState { Role = role };
            _roles[role] = state;
        }

        return state;
    }
}