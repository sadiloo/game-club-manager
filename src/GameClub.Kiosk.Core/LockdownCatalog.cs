namespace GameClub.Kiosk.Core;

public sealed record RegSetting(string Hive, string Key, string Name, string Kind, string Data, string Since);

/// <summary>
/// Registry policy the installers apply. Home editions have no gpedit.msc; these same keys
/// are what Group Policy would have written, and they exist on Windows 7 SP1 and later.
/// The shell value is per-user Winlogon, never the machine shell.
/// </summary>
public static class LockdownCatalog
{
    public const string PerUserShellKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public const string PerUserShellValue = "Shell";

    public const string MachineWinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public static IReadOnlyList<RegSetting> PerUser { get; } =
    [
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableTaskMgr", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableRegistryTools", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Policies\Microsoft\Windows\System", "DisableCMD", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableLockWorkstation", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableChangePassword", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoWinKeys", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoRun", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoControlPanel", "REG_DWORD", "1", "Win7+"),
        new("HKU", @"Control Panel\Accessibility\StickyKeys", "Flags", "REG_SZ", "506", "Win7+"),
        new("HKU", @"Control Panel\Accessibility\Keyboard Response", "Flags", "REG_SZ", "122", "Win7+"),
        new("HKU", @"Control Panel\Accessibility\ToggleKeys", "Flags", "REG_SZ", "58", "Win7+"),
        new("HKU", @"System\GameConfigStore", "GameDVR_Enabled", "REG_DWORD", "0", "Win10+"),
        new("HKU", @"Software\Microsoft\Windows\Windows Error Reporting", "DontShowUI", "REG_DWORD", "1", "Win7+"),
    ];

    public static IReadOnlyList<RegSetting> Machine { get; } =
    [
        new("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "HideFastUserSwitching", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", "REG_DWORD", "255", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoAutorun", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorUser", "REG_DWORD", "0", "Win7+"),
        new("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "REG_DWORD", "0", "Win10+"),
        new("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", "Deny_Execute", "REG_DWORD", "1", "Win7+"),
        new("HKLM", MachineWinlogonKey, "AutoAdminLogon", "REG_DWORD", "1", "Win7+"),
        new("HKLM", MachineWinlogonKey, "DontDisplayLastUserName", "REG_DWORD", "1", "Win7+"),
        new("HKLM", MachineWinlogonKey, "ForceAutoLogon", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\.NETFramework\v4.0.30319", "SchUseStrongCrypto", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319", "SchUseStrongCrypto", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\.NETFramework\v4.0.30319", "SystemDefaultTlsVersions", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319", "SystemDefaultTlsVersions", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp", "DefaultSecureProtocols", "REG_DWORD", "2560", "Win7+"),
        new("HKLM", @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp", "DefaultSecureProtocols", "REG_DWORD", "2560", "Win7+"),
        new("HKLM", @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client", "Enabled", "REG_DWORD", "1", "Win7+"),
        new("HKLM", @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client", "DisabledByDefault", "REG_DWORD", "0", "Win7+"),
    ];
}
