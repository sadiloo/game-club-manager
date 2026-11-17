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
        Assert.DoesNotContain("[^", text, StringComparison.Ordinal);
        Assert.DoesNotContain("..", text, StringComparison.Ordinal);
    }
}