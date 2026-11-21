using System.Text.RegularExpressions;

namespace GameClub.Server.Tests;

public sealed class InpageFormsOperatorWwwrootTests
{
    private static string WwwrootDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot"));

    [Fact]
    public void operator_wwwroot_has_no_window_dialogs()
    {
        var root = WwwrootDir();
        var pattern = new Regex(
            @"(window\.|globalThis\.|self\.)?\b(prompt|alert|confirm)\s*\(",
            RegexOptions.CultureInvariant);
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (ext is not ".js" and not ".html")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match m in pattern.Matches(text))
            {
                hits.Add($"{file.Replace(root, "").TrimStart(Path.DirectorySeparatorChar)}:{m.Value}");
            }
        }

        Assert.Empty(hits);
    }
}