namespace GameClub.Kiosk.Core;

/// <summary>
/// What the shell is allowed to start. Store URIs are an explicit list.
/// An executable must be an absolute path under an approved root, with no ".." segment.
/// http(s) and file URIs are rejected: a browser is a way out of the kiosk.
/// </summary>
public static class LaunchPolicy
{
    public static readonly IReadOnlyList<string> DefaultRoots = new[]
    {
        @"C:\Games",
        @"C:\Riot Games",
    };

    private static readonly string[] StoreSchemes = ["steam:", "com.epicgames.launcher:", "battlenet:"];

    public static string? Reject(string? launch, IReadOnlyList<string>? approvedRoots)
    {
        if (string.IsNullOrWhiteSpace(launch))
        {
            return "مسیر اجرای بازی خالی است.";
        }

        var text = launch!.Trim();
        if (IsStoreUri(text))
        {
            return null;
        }

        if (HasBannedScheme(text) || !IsDrivePath(text))
        {
            return "فقط فایل اجرایی داخل پوشهٔ بازی‌های مجاز، یا استیم و اپیک و بتل‌نت، قابل اجراست.";
        }

        if (!TryNormalize(text, out var normal))
        {
            return "مسیر بازی نامعتبر است.";
        }

        var roots = approvedRoots is { Count: > 0 } ? approvedRoots : DefaultRoots;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !TryNormalize(root, out var rootNormal))
            {
                continue;
            }

            if (IsUnder(normal, rootNormal))
            {
                return null;
            }
        }

        return "این بازی خارج از پوشه‌های مجاز است.";
    }

    public static bool IsStoreUri(string launch)
    {
        var text = launch.TrimStart();
        foreach (var scheme in StoreSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryNormalize(string path, out string normal)
    {
        normal = "";
        var text = path.Trim().Replace('/', '\\');
        if (!IsDrivePath(text))
        {
            return false;
        }

        var drive = char.ToUpperInvariant(text[0]);
        var rest = text.Substring(2).Trim('\\');
        var parts = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return false;
            }

            stack.Add(part);
        }

        normal = drive + ":\\" + string.Join("\\", stack);
        return true;
    }

    private static bool HasBannedScheme(string text)
    {
        if (IsDrivePath(text))
        {
            return false;
        }

        var colon = text.IndexOf(':');
        return colon > 0;
    }

    private static bool IsDrivePath(string text) =>
        text.Length >= 3
        && char.IsLetter(text[0])
        && text[1] == ':'
        && (text[2] == '\\' || text[2] == '/');

    private static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
