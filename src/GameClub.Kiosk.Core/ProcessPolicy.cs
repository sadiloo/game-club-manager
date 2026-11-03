namespace GameClub.Kiosk.Core;

public sealed record ProcessDecision(bool Kill, string Reason);

/// <summary>
/// Who the process belongs to. The guard only kills processes in the kiosk user's
/// own session. Session 0, SYSTEM, and any other user (including an admin at the
/// console) are left alone. A missing SID is not treated as "the kiosk user".
/// </summary>
public sealed record ProcessContext
{
    public int SessionId { get; init; }

    public string? UserSid { get; init; }

    public int KioskSessionId { get; init; }

    public string? KioskUserSid { get; init; }

    public bool Maintenance { get; init; }

    public string? ImagePath { get; init; }

    public string? OriginalFileName { get; init; }

    public bool SteamGameActive { get; init; }

    public IReadOnlyList<string>? AllowedRoots { get; init; }
}

/// <summary>
/// Known escape tools, matched on the process name, the image file name, and
/// FileVersionInfo.OriginalFilename so a renamed cmd.exe is still recognized.
/// This is a backstop for the per-user registry policies the Home editions honor
/// (DisableCMD, DisableRegistryTools, DisableTaskMgr). Software Restriction Policies
/// are not installed: they are a Group Policy feature, deprecated, and not something
/// this product can prove on Windows Home.
/// </summary>
public static class ProcessPolicy
{
    public const string SystemSid = "S-1-5-18";

    public const string LocalServiceSid = "S-1-5-19";

    public const string NetworkServiceSid = "S-1-5-20";

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr",
        "cmd",
        "powershell",
        "pwsh",
        "powershell_ise",
        "regedit",
        "reg",
        "mmc",
        "control",
        "msconfig",
        "utilman",
        "sethc",
        "wscript",
        "cscript",
        "mshta",
        "wt",
        "taskkill",
        "systemsettings",
        "cmdkey",
        "rundll32",
        "regsvr32",
        "hh",
        "msiexec",
        "wmic",
        "schtasks",
        "sc",
        "net",
        "net1",
        "runas",
        "certutil",
        "bitsadmin",
        "forfiles",
        "msbuild",
        "installutil",
        "csc",
        "bash",
        "wsl",
        "iexplore",
        "msedge",
        "microsoftedge",
        "chrome",
        "firefox",
        "opera",
        "brave",
        "vivaldi",
        "notepad",
        "wordpad",
        "write",
        "mspaint",
        "osk",
        "narrator",
        "magnify",
        "gamebar",
        "gamebarftw",
        "explorer++",
    };

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "iexplore",
        "msedge",
        "microsoftedge",
        "chrome",
        "firefox",
        "opera",
        "brave",
        "vivaldi",
        "waterfox",
        "palemoon",
    };

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "gameclub.kiosk",
        "gameclub.kiosk.guard",
    };

    public static ProcessDecision Evaluate(string processName, bool elevated, IEnumerable<string>? catalogProcesses, ProcessContext? context = null)
    {
        if (context is not null)
        {
            var scope = Scope(context);
            if (scope is not null)
            {
                return scope;
            }
        }

        var names = Names(processName, context);
        if (names.Count == 0)
        {
            return new ProcessDecision(false, "unnamed");
        }

        foreach (var name in names)
        {
            if (Protected.Contains(name))
            {
                return new ProcessDecision(false, "kiosk");
            }
        }

        if (context is { SteamGameActive: false })
        {
            foreach (var name in names)
            {
                if (string.Equals(name, "steamwebhelper", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessDecision(true, "steam_browser");
                }
            }
        }

        var calledExplorer = false;
        foreach (var name in names)
        {
            if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                calledExplorer = true;
            }
        }

        if (calledExplorer)
        {
            if (context is not null)
            {
                return new ProcessDecision(true, "explorer");
            }

            return elevated
                ? new ProcessDecision(false, "admin_explorer")
                : new ProcessDecision(true, "explorer");
        }

        foreach (var name in names)
        {
            if (Browsers.Contains(name))
            {
                return new ProcessDecision(true, "browser");
            }
        }

        foreach (var name in names)
        {
            if (Forbidden.Contains(name))
            {
                return new ProcessDecision(true, "forbidden");
            }
        }

        if (catalogProcesses is not null)
        {
            foreach (var allowed in catalogProcesses)
            {
                var normalized = Normalize(allowed);
                foreach (var name in names)
                {
                    if (string.Equals(normalized, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ProcessDecision(false, "catalog");
                    }
                }
            }
        }

        if (context?.ImagePath is string image && image.Length > 0)
        {
            if (LaunchPolicy.TryNormalize(image, out var normal) && !IsPlatformPath(normal) && !IsUnderRoots(normal, context.AllowedRoots))
            {
                return new ProcessDecision(true, "unlisted_path");
            }
        }

        return new ProcessDecision(false, "not_forbidden");
    }

    public static bool IsServiceAccount(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        var value = sid!;
        return string.Equals(value.Trim(), SystemSid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), LocalServiceSid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), NetworkServiceSid, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? processName)
    {
        if (processName is null)
        {
            return "";
        }

        var file = processName.Trim();
        if (file.Length == 0)
        {
            return "";
        }

        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (slash >= 0 && slash < file.Length - 1)
        {
            file = file.Substring(slash + 1);
        }

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            file = file.Substring(0, file.Length - 4);
        }

        return file;
    }

    private static ProcessDecision? Scope(ProcessContext context)
    {
        if (context.Maintenance)
        {
            return new ProcessDecision(false, "maintenance");
        }

        if (context.SessionId <= 0)
        {
            return new ProcessDecision(false, "session0");
        }

        if (IsServiceAccount(context.UserSid))
        {
            return new ProcessDecision(false, "service_account");
        }

        if (string.IsNullOrWhiteSpace(context.UserSid) || string.IsNullOrWhiteSpace(context.KioskUserSid))
        {
            return new ProcessDecision(false, "unidentified");
        }

        if (!string.Equals(context.UserSid!.Trim(), context.KioskUserSid!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessDecision(false, "other_user");
        }

        if (context.KioskSessionId > 0 && context.SessionId != context.KioskSessionId)
        {
            return new ProcessDecision(false, "other_session");
        }

        return null;
    }

    private static List<string> Names(string processName, ProcessContext? context)
    {
        var names = new List<string>();
        Add(names, Normalize(processName));
        if (context is not null)
        {
            Add(names, Normalize(context.OriginalFileName));
            Add(names, Normalize(context.ImagePath));
        }

        return names;
    }

    private static void Add(List<string> names, string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        foreach (var existing in names)
        {
            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        names.Add(name);
    }

    private static bool IsPlatformPath(string normal)
    {
        return normal.StartsWith(@"C:\WINDOWS\", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normal, @"C:\WINDOWS", StringComparison.OrdinalIgnoreCase)
            || normal.StartsWith(@"C:\PROGRAM FILES\", StringComparison.OrdinalIgnoreCase)
            || normal.StartsWith(@"C:\PROGRAM FILES (X86)\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoots(string normal, IReadOnlyList<string>? roots)
    {
        if (roots is null)
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (!LaunchPolicy.TryNormalize(root, out var normalRoot))
            {
                continue;
            }

            if (string.Equals(normal, normalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = normalRoot.EndsWith("\\", StringComparison.Ordinal) ? normalRoot : normalRoot + "\\";
            if (normal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
