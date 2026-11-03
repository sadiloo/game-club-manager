namespace GameClub.Kiosk.Core.Tests;

public class CatalogGuardTests
{
    private static readonly GameFilter Normal = new("pc-normal", 20);
    private static readonly GameFilter Minor = new("pc-normal", 15);

    [Fact]
    public void Launcher_lists_only_enabled_games_for_this_group_and_age()
    {
        var games = CafeKit.DemoGames();
        var adult = GameCatalog.Visible(games, Normal).Select(game => game.Id).ToArray();
        Assert.Equal(["local", "valorant", "cs2"], adult);
        Assert.DoesNotContain("hidden", adult);
        Assert.DoesNotContain("vip-only", adult);

        var minor = GameCatalog.Visible(games, Minor).Select(game => game.Id).ToArray();
        Assert.DoesNotContain("valorant", minor);
        Assert.Contains("local", minor);
    }

    [Fact]
    public void Unknown_age_hides_age_restricted_games()
    {
        var visible = GameCatalog.Visible(CafeKit.DemoGames(), new GameFilter("pc-normal", null));
        Assert.DoesNotContain(visible, game => game.Id == "valorant");
    }

    [Fact]
    public void Only_a_listed_visible_game_can_be_launched()
    {
        var games = CafeKit.DemoGames();
        var missing = GameCatalog.Prepare(games, "nope", Normal, WindowsGeneration.Windows10);
        Assert.False(missing.Allowed);

        var disabled = GameCatalog.Prepare(games, "hidden", Normal, WindowsGeneration.Windows10);
        Assert.False(disabled.Allowed);

        var vip = GameCatalog.Prepare(games, "vip-only", Normal, WindowsGeneration.Windows10);
        Assert.False(vip.Allowed);

        var local = GameCatalog.Prepare(games, "local", Normal, WindowsGeneration.Windows7);
        Assert.True(local.Allowed);
        Assert.Equal(@"C:\Games\Local\local.exe", local.FileName);
        Assert.Equal("-windowed", local.Arguments);
        Assert.Equal(@"C:\Games\Local", local.WorkingDirectory);
        Assert.False(local.UseShellExecute);

        var steam = GameCatalog.Prepare(games, "cs2", Normal, WindowsGeneration.Windows11);
        Assert.True(steam.Allowed);
        Assert.True(steam.UseShellExecute);
        Assert.StartsWith("steam://", steam.FileName);
    }

    [Fact]
    public void Steam_epic_and_battlenet_are_blocked_on_windows_7_and_8_1()
    {
        var games = CafeKit.DemoGames();
        Assert.False(GameCatalog.Prepare(games, "cs2", Normal, WindowsGeneration.Windows7).Allowed);
        Assert.False(GameCatalog.Prepare(games, "cs2", Normal, WindowsGeneration.Windows81).Allowed);
        Assert.True(GameCatalog.Prepare(games, "cs2", Normal, WindowsGeneration.Windows10).Allowed);

        var epic = new GameEntry { Id = "epic", Name = "فورت", Launch = "com.epicgames.launcher://apps/Fortnite?action=launch", Enabled = true };
        Assert.NotNull(GameCatalog.StoreLauncherBlock(epic.Launch, WindowsGeneration.Windows7));
        Assert.Null(GameCatalog.StoreLauncherBlock(epic.Launch, WindowsGeneration.Windows10));
        Assert.NotNull(GameCatalog.StoreLauncherBlock("battlenet://", WindowsGeneration.Windows81));
    }

    [Fact]
    public void A_catalog_entry_cannot_whitelist_cmd()
    {
        var evil = new GameEntry { Id = "evil", Name = "بد", Launch = @"C:\Windows\System32\cmd.exe", Enabled = true };
        var plan = GameCatalog.Prepare([evil], "evil", Normal, WindowsGeneration.Windows10);
        Assert.False(plan.Allowed);
    }

    [Fact]
    public void Guard_kills_escape_tools_and_allows_listed_games_and_launchers()
    {
        var allow = GameProcesses.AllowSet(CafeKit.DemoGames());
        Assert.Contains("steam", allow);
        Assert.Contains("steamwebhelper", allow);
        Assert.Contains("local", allow);
        Assert.Contains("easyanticheat", allow);
        Assert.Contains("vgc", allow);
        Assert.DoesNotContain("off", allow);

        Assert.Equal("forbidden", ProcessPolicy.Evaluate("taskmgr.exe", false, allow).Reason);
        Assert.True(ProcessPolicy.Evaluate(@"C:\Windows\System32\cmd.exe", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("powershell", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("pwsh.exe", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("regedit", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("utilman.exe", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("sethc", false, allow).Kill);
        Assert.True(ProcessPolicy.Evaluate("SystemSettings.exe", false, allow).Kill);

        Assert.Equal("explorer", ProcessPolicy.Evaluate("explorer.exe", elevated: false, allow).Reason);
        Assert.Equal("admin_explorer", ProcessPolicy.Evaluate("explorer.exe", elevated: true, allow).Reason);
        Assert.False(ProcessPolicy.Evaluate("explorer.exe", elevated: true, allow).Kill);

        Assert.Equal("catalog", ProcessPolicy.Evaluate("VALORANT.exe", false, allow).Reason);
        Assert.Equal("catalog", ProcessPolicy.Evaluate("steamwebhelper.exe", false, allow).Reason);
        Assert.Equal("catalog", ProcessPolicy.Evaluate("vgc", false, allow).Reason);
        Assert.Equal("catalog", ProcessPolicy.Evaluate("EasyAntiCheat.exe", false, allow).Reason);
        Assert.False(ProcessPolicy.Evaluate("vgtray.exe", false, allow).Kill);
        Assert.False(ProcessPolicy.Evaluate("faceitclient.exe", false, allow).Kill);
        Assert.False(ProcessPolicy.Evaluate("Battle.net.exe", false, allow).Kill);
        Assert.Equal("kiosk", ProcessPolicy.Evaluate("GameClub.Kiosk.exe", false, allow).Reason);
        Assert.False(ProcessPolicy.Evaluate("task", false, allow).Kill);
        Assert.Equal("unnamed", ProcessPolicy.Evaluate("  ", false, allow).Reason);
    }

    [Fact]
    public void Guard_restarts_a_dead_or_hung_shell_unless_admin_escape_or_backoff()
    {
        var now = CafeKit.At(12, 0);
        var options = new GuardOptions();
        Assert.Equal("shell_not_running", GuardAdvisor.Decide(new GuardSnapshot(false, null, null, []), now, options).Reason);
        Assert.True(GuardAdvisor.Decide(new GuardSnapshot(false, null, null, []), now, options).RestartShell);

        var waiting = GuardAdvisor.Decide(new GuardSnapshot(true, null, null, []), now, options);
        Assert.Equal("healthy", waiting.Reason);

        var hung = GuardAdvisor.Decide(new GuardSnapshot(true, now.AddSeconds(-30), null, []), now, options);
        Assert.Equal("heartbeat_timeout", hung.Reason);
        Assert.True(hung.EnforceBlocklist);

        var escape = GuardAdvisor.Decide(new GuardSnapshot(false, null, now.AddMinutes(5), []), now, options);
        Assert.Equal("admin_escape", escape.Reason);
        Assert.False(escape.RestartShell);
        Assert.False(escape.EnforceBlocklist);

        var restarts = Enumerable.Range(0, 5).Select(index => now.AddSeconds(-index)).ToArray();
        var backedOff = GuardAdvisor.Decide(new GuardSnapshot(false, null, null, restarts), now, options);
        Assert.Equal("restart_backoff", backedOff.Reason);
        Assert.False(backedOff.RestartShell);
        Assert.True(backedOff.EnforceBlocklist);
    }
}
