namespace GameClub.Server.Tests;

/// <summary>Unique prefixed Postgres database for PR52 HTTP LastSeen tests (R5).</summary>
internal sealed class Pr52R4HttpLastSeenTestDatabase : GameClubPrefixedTestDatabase
{
    public Pr52R4HttpLastSeenTestDatabase() : base("gameclub_test_pr52r5_")
    {
    }
}

public sealed class Pr52R4HttpLastSeenWebApplicationFactory : GameClubWebApplicationFactory
{
    private readonly Pr52R4HttpLastSeenTestDatabase _database = new();

    public Pr52R4HttpLastSeenWebApplicationFactory()
    {
        _database.EnsureInitialized();
    }

    protected override string? DedicatedConnectionString => _database.ConnectionString;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }

        base.Dispose(disposing);
    }
}

[CollectionDefinition("pr52-r4-http-lastseen", DisableParallelization = true)]
public sealed class Pr52R4HttpLastSeenCollection : ICollectionFixture<Pr52R4HttpLastSeenWebApplicationFactory>;