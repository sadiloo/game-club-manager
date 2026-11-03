namespace GameClub.Kiosk.Core;

/// <summary>
/// One game the cafe admin published. The kiosk never invents shortcuts: if it is not in this
/// list, it cannot be launched. <see cref="Launch"/> is an executable path or a launcher URI
/// such as <c>steam://rungameid/730</c>.
/// </summary>
public sealed record GameEntry
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string CoverPath { get; init; } = "";

    public string Launch { get; init; } = "";

    public string Arguments { get; init; } = "";

    public string WorkingDirectory { get; init; } = "";

    public string Category { get; init; } = "";

    public int? MinimumAge { get; init; }

    /// <summary>Empty means every station group. Otherwise only that group sees the game.</summary>
    public string? RestrictedGroupId { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Extra process names the guard must not treat as escapes (anti-cheat, helpers).</summary>
    public IReadOnlyList<string> HelperProcesses { get; init; } = [];
}

public sealed record GameFilter(string GroupId, int? MemberAge);

public sealed record LauncherGame(string Id, string Name, string Category, string CoverPath);

public sealed record LaunchPlan(
    bool Allowed,
    string? Error,
    string? FileName,
    string? Arguments,
    string? WorkingDirectory,
    bool UseShellExecute);

public static class GameCatalog
{
    public static IReadOnlyList<GameEntry> Normalize(IEnumerable<GameEntry>? games)
    {
        var list = new List<GameEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in games ?? [])
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Id) || !ids.Add(game.Id))
            {
                throw new InvalidOperationException("Each game needs a unique id.");
            }

            if (string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(game.Launch))
            {
                throw new InvalidOperationException($"Game '{game.Id}' needs a name and a launch path or URI.");
            }

            list.Add(game);
        }

        return list;
    }

    public static IReadOnlyList<LauncherGame> Visible(IEnumerable<GameEntry> games, GameFilter filter)
    {
        var visible = new List<LauncherGame>();
        foreach (var game in games)
        {
            if (!IsVisible(game, filter))
            {
                continue;
            }

            visible.Add(new LauncherGame(
                game.Id,
                game.Name,
                string.IsNullOrWhiteSpace(game.Category) ? "سایر" : game.Category,
                game.CoverPath ?? ""));
        }

        visible.Sort(static (left, right) =>
        {
            var category = string.Compare(left.Category, right.Category, StringComparison.Ordinal);
            return category != 0 ? category : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return visible;
    }

    public static bool IsVisible(GameEntry game, GameFilter filter)
    {
        if (!game.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(game.RestrictedGroupId)
            && !string.Equals(game.RestrictedGroupId, filter.GroupId, StringComparison.Ordinal))
        {
            return false;
        }

        if (game.MinimumAge is int minimum)
        {
            if (filter.MemberAge is not int age || age < minimum)
            {
                return false;
            }
        }

        return true;
    }

    public static LaunchPlan Prepare(
        IEnumerable<GameEntry> catalog,
        string gameId,
        GameFilter filter,
        WindowsGeneration operatingSystem)
    {
        GameEntry? game = null;
        foreach (var entry in catalog)
        {
            if (string.Equals(entry.Id, gameId, StringComparison.Ordinal))
            {
                game = entry;
                break;
            }
        }

        if (game is null)
        {
            return Deny("این بازی در فهرست کافه نیست.");
        }

        if (!IsVisible(game, filter))
        {
            return Deny("این بازی برای این سیستم یا این عضو فعال نیست.");
        }

        var osBlock = StoreLauncherBlock(game.Launch, operatingSystem);
        if (osBlock is not null)
        {
            return Deny(osBlock);
        }

        foreach (var process in GameProcesses.Names(game))
        {
            var decision = ProcessPolicy.Evaluate(process, elevated: false, catalogProcesses: []);
            if (decision.Kill)
            {
                return Deny("مسیر اجرای این بازی با یک برنامهٔ ممنوع یکی است.");
            }
        }

        var uri = GameProcesses.IsLauncherUri(game.Launch);
        var workingDirectory = game.WorkingDirectory ?? "";
        if (!uri && workingDirectory.Length == 0)
        {
            var folder = ParentFolder(game.Launch);
            workingDirectory = folder ?? "";
        }

        return new LaunchPlan(true, null, game.Launch, game.Arguments ?? "", workingDirectory, uri);
    }

    public static string? StoreLauncherBlock(string launch, WindowsGeneration operatingSystem)
    {
        if (operatingSystem is not (WindowsGeneration.Windows7 or WindowsGeneration.Windows8 or WindowsGeneration.Windows81))
        {
            return null;
        }

        if (GameProcesses.IsSteam(launch) || GameProcesses.IsEpic(launch) || GameProcesses.IsBattleNet(launch))
        {
            return "استیم، اپیک و بتل‌نت روی ویندوز ۷ و ۸.۱ نصب نمی‌شوند. این بازی به ویندوز ۱۰ یا ۱۱ نیاز دارد.";
        }

        return null;
    }

    private static LaunchPlan Deny(string error) => new(false, error, null, null, null, false);

    private static string? ParentFolder(string path)
    {
        var slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        if (slash <= 0)
        {
            return null;
        }

        return path.Substring(0, slash);
    }
}

public static class GameProcesses
{
    public static bool IsSteam(string launch) => HasPrefix(launch, "steam:");

    public static bool IsEpic(string launch) => HasPrefix(launch, "com.epicgames.launcher:");

    public static bool IsBattleNet(string launch) => HasPrefix(launch, "battlenet:");

    public static bool IsLauncherUri(string launch) => IsSteam(launch) || IsEpic(launch) || IsBattleNet(launch) || HasPrefix(launch, "http:") || HasPrefix(launch, "https:");

    public static IReadOnlyList<string> Names(GameEntry game)
    {
        var names = new List<string>();
        if (IsSteam(game.Launch))
        {
            names.Add("steam");
            names.Add("steamwebhelper");
        }
        else if (IsEpic(game.Launch))
        {
            names.Add("epicgameslauncher");
            names.Add("epicwebhelper");
        }
        else if (IsBattleNet(game.Launch))
        {
            names.Add("battle.net");
        }
        else if (!IsLauncherUri(game.Launch))
        {
            var file = ProcessPolicy.Normalize(game.Launch);
            if (file.Length > 0)
            {
                names.Add(file);
            }
        }

        foreach (var helper in game.HelperProcesses ?? [])
        {
            var normalized = ProcessPolicy.Normalize(helper);
            if (normalized.Length > 0)
            {
                names.Add(normalized);
            }
        }

        return names;
    }

    public static HashSet<string> AllowSet(IEnumerable<GameEntry> games)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            if (!game.Enabled)
            {
                continue;
            }

            foreach (var name in Names(game))
            {
                set.Add(name);
            }
        }

        return set;
    }

    private static bool HasPrefix(string value, string prefix) =>
        !string.IsNullOrWhiteSpace(value)
        && value.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
