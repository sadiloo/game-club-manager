using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Core.Security;

public static class KioskTrustFileClientFactory
{
    public const string DirectWriteEnvVar = "GAMECLUB_KIOSK_TRUST_DIRECT_WRITE";
    public const string UseGuardPipeEnvVar = "CHECKPOINT_KIOSK_TRUST_PIPE";

    public static IKioskTrustFileClient ForPath(string absolutePath)
    {
        if (!KioskTrustFiles.IsTrustFilePath(absolutePath))
        {
            return new KioskTrustFileDirectClient(mode: TrustWriteMode.Unrestricted);
        }

        if (ShouldUseDirectWrite(absolutePath))
        {
            return new KioskTrustFileDirectClient(TrustWriteMode.ExplicitFallback);
        }

#if NET10_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            return new KioskTrustFileDirectClient(TrustWriteMode.ExplicitFallback);
        }
#endif
        return new KioskTrustFilePipeClient();
    }

    internal static bool ShouldUseDirectWrite(string absolutePath)
    {
        _ = absolutePath;
        if (string.Equals(Environment.GetEnvironmentVariable(DirectWriteEnvVar), "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (KioskLegacyDevPath.IsActive())
        {
            return true;
        }

        if (!IsWindows())
        {
            return true;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(UseGuardPipeEnvVar), "0", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsWindows()
    {
#if NET10_0_OR_GREATER
        return OperatingSystem.IsWindows();
#else
        var platform = Environment.OSVersion.Platform;
        return platform == PlatformID.Win32NT
            || platform == PlatformID.Win32S
            || platform == PlatformID.Win32Windows
            || platform == PlatformID.WinCE;
#endif
    }
}