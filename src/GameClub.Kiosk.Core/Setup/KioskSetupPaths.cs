namespace GameClub.Kiosk.Core.Setup;

public static class KioskSetupPaths
{
    public static string KioskDataRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");

    public static string SetupJsonPath => Path.Combine(KioskDataRoot, "setup.json");
}