namespace GameClub.Server.Tests;

public sealed class Ss14FixR1fVisibilityNodeTests
{
    [Fact]
    public async Task FixR1f_manager_visibility_jsdom_selftest()
    {
        var r = await NodeScriptRunner.RunScriptAsync(NodeScriptRunner.Script("ss14-manager-visibility-selftest.mjs"), TimeSpan.FromSeconds(60));
        NodeScriptRunner.AssertOk(r, "ok");
    }
}
