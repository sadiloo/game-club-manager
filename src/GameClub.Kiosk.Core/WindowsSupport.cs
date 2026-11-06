using GameClub.Billing;

namespace GameClub.Kiosk.Core;

public enum WindowsGeneration
{
    Unknown = 0,
    Windows7 = 7,
    Windows8 = 8,
    Windows81 = 81,
    Windows10 = 10,
    Windows11 = 11,
}

public enum KioskOsSupport
{
    Supported = 0,
    UpgradeWindows8To81 = 1,
    TooOld = 2,
}

/// <summary>
/// Version gates for the kiosk. .NET Framework 4.8 runs on Windows 7 SP1 and Windows 8.1,
/// not on the original Windows 8 release. ClientWebSocket exists only from Windows 8 onward,
/// so Windows 7 must not ask SignalR for WebSockets.
/// </summary>
public static class WindowsSupport
{
    public static KioskOsSupport Classify(Version version)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (version.Major < 6 || (version.Major == 6 && version.Minor < 1))
        {
            return KioskOsSupport.TooOld;
        }

        if (version.Major == 6 && version.Minor == 2)
        {
            return KioskOsSupport.UpgradeWindows8To81;
        }

        return KioskOsSupport.Supported;
    }

    public static WindowsGeneration Generation(Version version, int build = 0)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (version.Major == 6 && version.Minor == 1)
        {
            return WindowsGeneration.Windows7;
        }

        if (version.Major == 6 && version.Minor == 2)
        {
            return WindowsGeneration.Windows8;
        }

        if (version.Major == 6 && version.Minor == 3)
        {
            return WindowsGeneration.Windows81;
        }

        if (version.Major >= 10)
        {
            return build >= 22000 ? WindowsGeneration.Windows11 : WindowsGeneration.Windows10;
        }

        return WindowsGeneration.Unknown;
    }

    /// <summary>
    /// .NET Framework <c>ClientWebSocket</c> is unsupported on Windows 7 (6.1) and throws
    /// <see cref="PlatformNotSupportedException"/> when used. Windows 8 (6.2) and later have it.
    /// </summary>
    public static bool ClientWebSocketSupported(Version version)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        return version >= new Version(6, 2);
    }

    public static DateTimeOffset TehranNow(DateTimeOffset clock) => TehranTime.FromUtc(clock.UtcDateTime);
}
