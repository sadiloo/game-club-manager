namespace GameClub.Kiosk.Core.Setup;

public static class KioskSetupPaths
{
    /// <summary>When set (tests/CI), overrides <see cref="SetupJsonPath"/>; production leaves this unset.</summary>
    public const string SetupJsonPathEnvVar = "GAMECLUB_KIOSK_SETUP_JSON";

    public static string KioskDataRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");

    public static string SetupJsonPath =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SetupJsonPathEnvVar))
            ? Path.Combine(KioskDataRoot, "setup.json")
            : Environment.GetEnvironmentVariable(SetupJsonPathEnvVar)!;
}