using System.Text.RegularExpressions;

namespace GameClub.Server.Tests;

/// <summary>Guards operator-facing ApiException literals stay Persian (kiosk semantic keys exempt in Kiosk/).</summary>
public sealed class InpageFormsApiExceptionPersianSweepTests
{
    private static IEnumerable<string> OperatorReachableSources()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "GameClub.Server"));
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("Kiosk/", StringComparison.Ordinal)
                || rel.Contains("/obj/", StringComparison.Ordinal)
                || rel.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return rel;
        }
    }

    private static void AssertNoEnglishApiExceptionLiterals(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "GameClub.Server", relative));
        var src = File.ReadAllText(path);
        var pattern = new Regex(
            @"throw\s+(?:new\s+)?(?:Services\.)?ApiException\([^)]*,\s*""([^""]+)""\s*\)",
            RegexOptions.Multiline);
        foreach (Match m in pattern.Matches(src))
        {
            var msg = m.Groups[1].Value;
            if (!Regex.IsMatch(msg, @"[A-Za-z]"))
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(msg, @"[\u0600-\u06FF]"),
                $"{relative}: English ApiException message must be Persian: \"{msg}\"");
        }
    }

    [Theory]
    [MemberData(nameof(AllOperatorReachableSources))]
    public void operator_reachable_sources_have_persian_api_exception_messages(string relative)
    {
        AssertNoEnglishApiExceptionLiterals(relative);
    }

    public static IEnumerable<object[]> AllOperatorReachableSources()
        => OperatorReachableSources().Select(r => new object[] { r });
}