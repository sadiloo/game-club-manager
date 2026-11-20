namespace GameClub.Server.Data.Entities;

public sealed class KioskCredentialEntity
{
    public string Role { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}