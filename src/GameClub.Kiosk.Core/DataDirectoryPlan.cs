namespace GameClub.Kiosk.Core;

/// <summary>
/// %ProgramData%\GameClub holds the database, the admin hash, and the station secret.
/// The kiosk user must not create or read those files. SYSTEM and Administrators own the directory.
/// </summary>
public static class DataDirectoryPlan
{
    public const string FolderName = "GameClub";

    public const string AclInheritanceRemoved = "/inheritance:r";

    public const string SystemFullControl = "SYSTEM:(OI)(CI)F";

    public const string AdministratorsFullControl = "*S-1-5-32-544:(OI)(CI)F";

    public const string BackupFolder = "backup";

    public const string MaintenanceFlag = "maintenance.flag";

    public static readonly string[] ProtectedFiles =
    [
        "kiosk.db",
        "admin-gate.json",
        "admin-lockout.json",
        "kiosk.json",
        "station.token",
        "shell.path",
        "kiosk.sid",
        "guard.json",
    ];
}
