namespace GameClub.Server.Tests;

public sealed class TestDatabaseGuardTests
{
    [Theory]
    [InlineData("gameclub_test")]
    [InlineData("gameclub")]
    [InlineData("postgres")]
    [InlineData("")]
    public void ensure_deleted_guard_rejects_unprefixed_or_shared_names(string name)
    {
        Assert.ThrowsAny<InvalidOperationException>(() => TestDatabaseGuard.EnsureDeletableDatabaseName(name));
    }

    [Fact]
    public void ensure_deleted_guard_allows_prefixed_unique_name()
    {
        TestDatabaseGuard.EnsureDeletableDatabaseName("gameclub_test_a1b2c3d4e5f6");
    }
}