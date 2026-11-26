namespace GameClub.Server.Tests;

public sealed class KioskOpFreeIdleUiSelftestTests
{
    [Fact]
    public async Task kiosk_op_free_idle_ui_selftest_shows_persian_without_mgr_keys()
    {
        var r = await NodeScriptRunner.RunScriptAsync(NodeScriptRunner.Script("kiosk-op-free-idle-selftest.mjs"), TimeSpan.FromSeconds(60));
        NodeScriptRunner.AssertOk(r, "ok");
    }
}
