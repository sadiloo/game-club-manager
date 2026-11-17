namespace GameClub.Billing.Tests;

public sealed class Ss14FixR1NetStandardSyntaxTests
{
    [Fact]
    public void FixR1_F1_sessions_cs_has_no_index_or_range_syntax()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "GameClub.Billing", "Sessions.cs"));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("[" + "^", text, StringComparison.Ordinal);
        Assert.DoesNotContain("..", text, StringComparison.Ordinal);

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (var root in new[] { Path.Combine(repoRoot, "src", "GameClub.Billing"), Path.Combine(repoRoot, "tests", "GameClub.Billing.Tests") })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), nameof(Ss14FixR1NetStandardSyntaxTests) + ".cs", StringComparison.Ordinal))
                    continue;

                var source = File.ReadAllText(file);
                Assert.False(HasIndexFromEnd(source), $"Index-from-end syntax in {file}");
                Assert.False(HasRangeIndexer(source), $"Range indexer syntax in {file}");
            }
        }
    }

    private static bool HasIndexFromEnd(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '[' && text[i + 1] == '^')
                return true;
        }

        return false;
    }

    private static bool HasRangeIndexer(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '[')
                continue;
            var close = text.IndexOf(']', i + 1);
            if (close < 0)
                continue;
            if (text.AsSpan(i + 1, close - i - 1).Contains("..", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}