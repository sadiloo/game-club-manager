#requires -Version 2
<#
.SYNOPSIS
  Stores or clears the kiosk autologon password as an LSA secret.
  The password is never written to Winlogon as a DefaultPassword value.
#>
param(
  [string]$SecretName = "DefaultPassword",
  [string]$SecretFile = "",
  [switch]$Clear
)

$ErrorActionPreference = "Stop"
$member = @"
using System;
using System.Runtime.InteropServices;

public class GameClubLsaSecret {
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern uint LsaOpenPolicy(IntPtr system, ref LsaObjectAttributes attr, uint access, out IntPtr handle);
  [DllImport("advapi32.dll", EntryPoint="LsaStorePrivateData", CharSet=CharSet.Unicode)]
  public static extern uint LsaStore(IntPtr handle, ref LsaUnicodeString key, ref LsaUnicodeString value);
  [DllImport("advapi32.dll", EntryPoint="LsaStorePrivateData", CharSet=CharSet.Unicode)]
  public static extern uint LsaClear(IntPtr handle, ref LsaUnicodeString key, IntPtr value);
  [DllImport("advapi32.dll")]
  public static extern uint LsaClose(IntPtr handle);
  [DllImport("advapi32.dll")]
  public static extern uint LsaNtStatusToWinError(uint status);

  [StructLayout(LayoutKind.Sequential)]
  public struct LsaUnicodeString {
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct LsaObjectAttributes {
    public int Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;
  }

  public static void Store(string key, string value) {
    Run(key, value, false);
  }

  public static void Clear(string key) {
    Run(key, null, true);
  }

  static void Run(string key, string value, bool clear) {
    LsaObjectAttributes attr = new LsaObjectAttributes();
    attr.Length = Marshal.SizeOf(typeof(LsaObjectAttributes));
    IntPtr handle;
    uint status = LsaOpenPolicy(IntPtr.Zero, ref attr, 0x000F0FFF, out handle);
    if (status != 0) throw new InvalidOperationException("LsaOpenPolicy " + LsaNtStatusToWinError(status));
    try {
      LsaUnicodeString name = Alloc(key);
      if (clear) {
        status = LsaClear(handle, ref name, IntPtr.Zero);
      } else {
        LsaUnicodeString data = Alloc(value);
        status = LsaStore(handle, ref name, ref data);
      }
      if (status != 0) throw new InvalidOperationException("LsaStorePrivateData " + LsaNtStatusToWinError(status));
    } finally {
      LsaClose(handle);
    }
  }

  static LsaUnicodeString Alloc(string text) {
    LsaUnicodeString item = new LsaUnicodeString();
    item.Buffer = Marshal.StringToHGlobalUni(text);
    item.Length = (ushort)(text.Length * 2);
    item.MaximumLength = (ushort)((text.Length + 1) * 2);
    return item;
  }
}
"@
Add-Type -TypeDefinition $member -Language CSharp
if ($Clear) {
  [GameClubLsaSecret]::Clear($SecretName)
  exit 0
}
if ($SecretFile -eq "") {
  Write-Error "Pass -SecretFile or -Clear."
}
$secret = Get-Content -LiteralPath $SecretFile | Select-Object -First 1
[GameClubLsaSecret]::Store($SecretName, [string]$secret)
exit 0
