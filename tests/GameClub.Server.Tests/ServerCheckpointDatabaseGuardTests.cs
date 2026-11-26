namespace GameClub.Server.Tests;

public sealed class ServerCheckpointDatabaseGuardTests
{
    [Fact]
    public void gameclub_test_database_named_checkpoint_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServerApplication.RefuseLiveCheckpointTestDatabase(
                "Host=localhost;Port=5432;Database=checkpoint;Username=postgres;Password=secret"));
        Assert.Contains("Refusing to use the live checkpoint database", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void gameclub_test_database_prefixed_name_is_allowed()
    {
        ServerApplication.RefuseLiveCheckpointTestDatabase(
            "Host=localhost;Port=5432;Database=gameclub_test_a1b2c3d4;Username=gameclub;Password=gameclub");
    }
}