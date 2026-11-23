using System.Diagnostics;

namespace GameClub.Kiosk.Core.Security;

public enum TrustWriteMode
{
    Unrestricted,
    ExplicitFallback,
}

public sealed class KioskTrustFileDirectClient : IKioskTrustFileClient
{
    private readonly TrustWriteMode _mode;

    public KioskTrustFileDirectClient(TrustWriteMode mode = TrustWriteMode.ExplicitFallback)
        => _mode = mode;

    public void WriteTrustFile(string absolutePath, string jsonContent)
    {
        if (_mode == TrustWriteMode.ExplicitFallback)
        {
            TrustWriteDiagnostics.LogDirectFallback(absolutePath);
        }

        var dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        KioskTrustFileAtomicWrite.WriteText(absolutePath, jsonContent);
    }
}

internal static class TrustWriteDiagnostics
{
    internal static void LogDirectFallback(string path)
        => Trace.TraceWarning($"kiosk.trust.write.direct_fallback path={path}");

    internal static void LogPipe(string path)
        => Console.Error.WriteLine($"kiosk.trust.write.guard_pipe path={path}");
}