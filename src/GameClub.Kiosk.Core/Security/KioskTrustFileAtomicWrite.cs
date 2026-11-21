namespace GameClub.Kiosk.Core.Security;

public static class KioskTrustFileAtomicWrite
{
    public static void WriteText(string destinationPath, string content)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content);
        if (File.Exists(destinationPath))
        {
            var backup = destinationPath + ".bak";
            File.Replace(temp, destinationPath, backup, ignoreMetadataErrors: true);
            try
            {
                File.Delete(backup);
            }
            catch
            {
                // best effort
            }
        }
        else
        {
            File.Move(temp, destinationPath);
        }
    }
}