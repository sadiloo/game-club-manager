namespace GameClub.Server.Tests;

public sealed class Ss14FixR1eLabelTests
{
    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    private static string Ss14RenderManagerFormBlock()
    {
        var js = File.ReadAllText(AppJsPath());
        var start = js.IndexOf("function ss14RenderManagerForm(title)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = js.IndexOf("async function ss14LoadManagerData", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return js[start..end];
    }

    [Fact]
    public void FixR1e_each_console_option_row_emits_visible_labels_and_aria_labels()
    {
        const string hourly = "نرخ عادی (تومان در ساعت)";
        const string night = "نرخ ساعت خاص (تومان در ساعت)";
        var block = Ss14RenderManagerFormBlock();

        Assert.Contains("for (const k of ss14MgrConsoleKeys)", block, StringComparison.Ordinal);
        Assert.Contains("createElement(\"label\")", block, StringComparison.Ordinal);
        Assert.Contains("htmlFor", block, StringComparison.Ordinal);
        Assert.Contains(hourly, block, StringComparison.Ordinal);
        Assert.Contains(night, block, StringComparison.Ordinal);
        Assert.Contains("setAttribute(\"aria-label\", lblHourlyText)", block, StringComparison.Ordinal);
        Assert.Contains("setAttribute(\"aria-label\", lblNightText)", block, StringComparison.Ordinal);

        var loopStart = block.IndexOf("for (const k of ss14MgrConsoleKeys)", StringComparison.Ordinal);
        var loopMarker = block.IndexOf("body.appendChild(tr);", loopStart, StringComparison.Ordinal);
        Assert.True(loopMarker > loopStart);
        var loopBody = block[loopStart..(loopMarker + "body.appendChild(tr);".Length)];

        Assert.Equal(2, CountOccurrences(loopBody, "createElement(\"label\")"));
        Assert.Equal(2, CountOccurrences(loopBody, "setAttribute(\"aria-label\""));
        Assert.Contains("lblH.textContent = lblHourlyText", loopBody, StringComparison.Ordinal);
        Assert.Contains("lblN.textContent = lblNightText", loopBody, StringComparison.Ordinal);
        Assert.Contains("tdH.append(lblH, inH)", loopBody, StringComparison.Ordinal);
        Assert.Contains("tdN.append(lblN, inN)", loopBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FixR1e_manager_save_error_placed_in_edit_form_near_save_button()
    {
        var js = File.ReadAllText(AppJsPath());
        var panelStart = js.IndexOf("function ss14EnsureManagerPanel()", StringComparison.Ordinal);
        Assert.True(panelStart >= 0);
        var panelBlock = js[panelStart..(panelStart + 1400)];
        Assert.Contains("id=\"ss14GameRatesError\"", panelBlock, StringComparison.Ordinal);
        Assert.Contains("id=\"ss14GameEditForm\"", panelBlock, StringComparison.Ordinal);
        Assert.Contains("function ss14ShowManagerError", js, StringComparison.Ordinal);
        var saveIdx = panelBlock.IndexOf("ss14GameSaveBtn", StringComparison.Ordinal);
        var errIdx = panelBlock.IndexOf("ss14GameRatesError", StringComparison.Ordinal);
        Assert.True(saveIdx >= 0 && errIdx > saveIdx);

        var styleStart = js.IndexOf("function ss14EnsureManagerPanelStyles", StringComparison.Ordinal);
        var styleBlock = js[styleStart..(styleStart + 1400)];
        Assert.Contains("ss14-game-rates-error", styleBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void FixR1e_manager_panel_styles_label_readable_at_narrow_width()
    {
        var js = File.ReadAllText(AppJsPath());
        var start = js.IndexOf("function ss14EnsureManagerPanelStyles", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = js[start..(start + 1200)];
        Assert.Contains("ss14-rate-cell label", block, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap:anywhere", block, StringComparison.Ordinal);
        Assert.Contains("max-width:100%", block, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var idx = 0;
        while (true)
        {
            var found = text.IndexOf(value, idx, StringComparison.Ordinal);
            if (found < 0) break;
            count++;
            idx = found + value.Length;
        }
        return count;
    }
}