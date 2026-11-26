namespace GameClub.Server.Tests;

public sealed class Ss14FixR1eJsRateParserTests
{
    [Fact]
    public async Task FixR1e_shipped_app_js_rate_parser_grouping_via_node()
    {
        // Same as before: run from the test process working directory, not the Scripts folder.
        var r = await NodeScriptRunner.RunScriptAsync(
            NodeScriptRunner.Script("ss14-shipped-rate-parser-selftest.mjs"), TimeSpan.FromSeconds(60), Environment.CurrentDirectory);
        NodeScriptRunner.AssertOk(r, "ok");
    }
}
