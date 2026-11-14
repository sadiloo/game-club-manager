namespace GameClub.Kiosk.Core.Security;

/// <summary>Whether the Explorer DisallowRun child key exists and its numbered entries.</summary>
public readonly struct DisallowRunPolicySnapshot
{
    public bool ChildKeyMissing { get; init; }

    public IReadOnlyList<string>? Entries { get; init; }

    public static DisallowRunPolicySnapshot MissingChild => new() { ChildKeyMissing = true };

    public static DisallowRunPolicySnapshot WithEntries(IReadOnlyList<string> entries) =>
        new() { Entries = entries };
}
