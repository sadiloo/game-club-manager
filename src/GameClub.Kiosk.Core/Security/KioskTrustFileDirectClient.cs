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
    {
        const string message = "kiosk.trust.write.direct_fallback";
        Trace.TraceWarning($"{message} path={path}");
#if NET5_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
#else
        if (Environment.OSVersion.Platform != PlatformID.Win32NT
            && Environment.OSVersion.Platform != PlatformID.Win32Windows)
        {
            return;
        }
#endif
        try
        {
            const string source = "CheckpointKiosk";
            if (!EventLog.SourceExists(source))
            {
                EventLog.CreateEventSource(source, "Application");
            }

            EventLog.WriteEntry(source, $"{message} path={path}", EventLogEntryType.Warning, 9010);
        }
        catch
        {
            // best effort alongside Trace
        }
    }

    internal static void LogPipe(string path)
        => Console.Error.WriteLine($"kiosk.trust.write.guard_pipe path={path}");
}