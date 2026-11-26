using System;
using System.IO;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardPaths
{
    public static string ProgramDataGuard =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "guard");

    public static string ConfigPath => Path.Combine(ProgramDataGuard, "internet.json");
    public static string StatePath => Path.Combine(ProgramDataGuard, "internet-state.json");
    public static string LogPath => Path.Combine(ProgramDataGuard, "internet.log");
}