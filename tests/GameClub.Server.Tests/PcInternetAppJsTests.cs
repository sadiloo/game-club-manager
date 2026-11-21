namespace GameClub.Server.Tests;

public sealed class PcInternetAppJsTests
{
    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    [Fact]
    public void PcInternet_confirm_dialog_strings_present()
    {
        var js = File.ReadAllText(AppJsPath());
        Assert.Contains("قطع اینترنت", js, StringComparison.Ordinal);
        Assert.Contains("وصل اینترنت", js, StringComparison.Ordinal);
        Assert.Contains("از همین لحظه نرخ می‌شود", js, StringComparison.Ordinal);
        Assert.Contains("commandId", js, StringComparison.Ordinal);
        Assert.Contains("/internet", js, StringComparison.Ordinal);
    }

    [Fact]
    public void PcInternet_grid_chip_disconnect_label()
    {
        var js = File.ReadAllText(AppJsPath());
        Assert.Contains("قطع از سرور", js, StringComparison.Ordinal);
        Assert.Contains("pcInternetRenderTileChips", js, StringComparison.Ordinal);
    }

    [Fact]
    public void PcInternet_manager_pc_rates_panel()
    {
        var js = File.ReadAllText(AppJsPath());
        Assert.Contains("pcRates", js, StringComparison.Ordinal);
        Assert.Contains("نرخ آفلاین روز و شب را با هم وارد کنید", js, StringComparison.Ordinal);
        Assert.Contains("ss14ParseTomanInput", js, StringComparison.Ordinal);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"offlineDay == null !=="), js);
    }

    [Fact]
    public void PcInternet_pc_rates_jsdom_selftest_passes()
    {
        var script = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "tests", "GameClub.Server.Tests", "Scripts", "pc-rates-shipped-parser-selftest.mjs"));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30_000);
        var output = proc.StandardOutput.ReadToEnd();
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("pc-rates-shipped-parser-selftest: ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PcInternet_addendum_nextBand_confirm_and_stale_refresh()
    {
        var js = File.ReadAllText(AppJsPath());
        Assert.Contains("nextBand", js, StringComparison.Ordinal);
        Assert.Contains("pcInternetRefreshManagerRatesUi", js, StringComparison.Ordinal);
        Assert.Contains("stale_version", js, StringComparison.Ordinal);
        Assert.Contains("pcInternetErrorMessage", js, StringComparison.Ordinal);
    }
}