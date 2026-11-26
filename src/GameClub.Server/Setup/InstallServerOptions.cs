namespace GameClub.Server.Setup;

public sealed class InstallServerOptions
{
    public const string SectionName = "Checkpoint";

    public string? ServerId { get; set; }
}

public sealed class InitialUserEntry
{
    public string Username { get; set; } = "";

    public string Role { get; set; } = "";

    public string PinHash { get; set; } = "";
}