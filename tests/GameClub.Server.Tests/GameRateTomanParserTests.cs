using GameClub.Server.Services;

namespace GameClub.Server.Tests;

public sealed class GameRateTomanParserTests
{
    [Theory]
    [InlineData("150,00")]
    [InlineData("1,,5")]
    [InlineData(",150000")]
    [InlineData("150000,")]
    [InlineData("1,50,000")]
    public void FixR1e_invalid_thousands_grouping_rejected(string raw)
    {
        Assert.False(GameRateTomanParser.TryParseStrict(raw, out _));
    }

    [Theory]
    [InlineData("150000", 150000L)]
    [InlineData("150,000", 150000L)]
    [InlineData("۱۵۰٬۰۰۰", 150000L)]
    [InlineData("1,500,000", 1_500_000L)]
    public void FixR1e_valid_thousands_grouping_parsed(string raw, long expected)
    {
        Assert.True(GameRateTomanParser.TryParseStrict(raw, out var toman));
        Assert.Equal(expected, toman);
    }
}