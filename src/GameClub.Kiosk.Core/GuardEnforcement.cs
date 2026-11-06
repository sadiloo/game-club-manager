namespace GameClub.Kiosk.Core;

/// <summary>
/// The guard's kill decision: the deny list, plus the names the session controller
/// asked to stop when credit ran out or the member logged out.
/// </summary>
public static class GuardEnforcement
{
    public static ProcessDecision Combine(
        string processName,
        bool elevated,
        IEnumerable<string>? catalogProcesses,
        ProcessContext? context,
        IEnumerable<string>? stopNames)
    {
        var decision = ProcessPolicy.Evaluate(processName, elevated, catalogProcesses, context);
        if (decision.Kill || IsOutOfScope(decision.Reason))
        {
            return decision;
        }

        var name = ProcessPolicy.Normalize(processName);
        if (stopNames is null || name.Length == 0)
        {
            return decision;
        }

        foreach (var stop in stopNames)
        {
            if (string.Equals(ProcessPolicy.Normalize(stop), name, StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessDecision(true, "session_end");
            }
        }

        return decision;
    }

    private static bool IsOutOfScope(string reason) =>
        reason is "other_user" or "session0" or "service_account" or "other_session" or "maintenance" or "unidentified";
}
