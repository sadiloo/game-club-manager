namespace GameClub.Server.Tests;

public sealed class Ss14FixR1AppJsTests
{
    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    [Fact]
    public void FixR1_F6_player_count_filter_resets_when_session_has_no_game()
    {
        var js = File.ReadAllText(AppJsPath());
        var start = js.IndexOf("async function ss14FilterPlayerCountSelect", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = js[start..(start + 900)];
        Assert.Contains("!tile?.currentGameTitleId", block, StringComparison.Ordinal);
        Assert.Contains("opt.disabled = false", block, StringComparison.Ordinal);
        Assert.Contains("currentGameRatedPlayerCounts", block, StringComparison.Ordinal);
    }

    [Fact]
    public void FixR1_F9_invalid_rate_input_raises_persian_error_not_silent_delete()
    {
        var js = File.ReadAllText(AppJsPath());
        Assert.Contains("مقدار نرخ عادی نامعتبر است", js, StringComparison.Ordinal);
        Assert.Contains("if (hourlyTrim === \"\") continue", js, StringComparison.Ordinal);
        Assert.Contains(@"\u0660-\u0669", js, StringComparison.Ordinal);
    }

    [Fact]
    public void FixR1_F7_game_picker_confirm_catches_api_errors()
    {
        var js = File.ReadAllText(AppJsPath());
        var start = js.IndexOf("async function ss14ShowGamePicker", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = js[start..(start + 1800)];
        Assert.Contains("try {", block, StringComparison.Ordinal);
        Assert.Contains("if (onConfirm) await onConfirm", block, StringComparison.Ordinal);
        Assert.Contains("catch (_)", block, StringComparison.Ordinal);
    }
}