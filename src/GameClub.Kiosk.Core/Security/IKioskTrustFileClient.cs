namespace GameClub.Kiosk.Core.Security;

public interface IKioskTrustFileClient
{
    void WriteTrustFile(string absolutePath, string jsonContent);
}