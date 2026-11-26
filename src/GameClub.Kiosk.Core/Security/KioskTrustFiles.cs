namespace GameClub.Kiosk.Core.Security;

public static class KioskTrustFiles
{
    public const string PipeName = "CheckpointKioskTrust";
    public const string SetupFileName = "setup.json";
    public const string AdminLockFileName = "admin-lock.json";
    public const int MaxContentBytes = 256 * 1024;

    public static string TrustRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");

    public static bool IsTrustFilePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return false;
        }

        var fileName = Path.GetFileName(absolutePath);
        if (!string.Equals(fileName, SetupFileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, AdminLockFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var root = Path.GetFullPath(TrustRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(Path.GetFullPath(absolutePath))
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
    }

    public static string TrustFilePath(string fileName)
        => Path.Combine(TrustRoot, fileName);
}