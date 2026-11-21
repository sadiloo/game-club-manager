namespace GameClub.Server.Tests;

/// <summary>Unique prefixed Postgres database for kiosk attach seam tests (R6).</summary>
internal sealed class KioskAttachPathSeamTestDatabase : GameClubPrefixedTestDatabase
{
    public KioskAttachPathSeamTestDatabase() : base("gameclub_test_kiosk_attach_")
    {
    }
}

public sealed class KioskAttachPathSeamWebApplicationFactory : GameClubWebApplicationFactory
{
    private readonly KioskAttachPathSeamTestDatabase _database = new();

    public KioskAttachPathSeamWebApplicationFactory()
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