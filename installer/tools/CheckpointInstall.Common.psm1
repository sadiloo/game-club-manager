# Shared kiosk installer helpers (R8b L-3). R8c may dot-source this module.

function Set-KioskTrustFileAcl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$KioskAccount,
        [string]$Root = (Join-Path $env:ProgramData 'Checkpoint\kiosk')
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        New-Item -ItemType Directory -Force -Path $Root | Out-Null
    }

    $kioskSid = (New-Object System.Security.Principal.NTAccount($KioskAccount)).Translate(
        [System.Security.Principal.SecurityIdentifier])
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18'
    $adminSid = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'

    $trustNames = @('setup.json', 'admin-lock.json')
    foreach ($name in $trustNames) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path)) {
            [System.IO.File]::WriteAllText($path, '')
        }

        $item = Get-Item -LiteralPath $path -Force
        $owner = New-Object System.Security.Principal.NTAccount 'NT AUTHORITY\SYSTEM'
        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetOwner($owner)
        $acl.SetAccessRuleProtection($true, $false)

        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $systemSid, 'FullControl', 'None', 'None', 'Allow')))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $adminSid, 'FullControl', 'None', 'None', 'Allow')))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                $kioskSid, 'Write,Delete,WriteData,AppendData', 'None', 'None', 'Deny')))
        $item.SetAccessControl($acl)
    }

    $dir = Get-Item -LiteralPath $Root -Force
    $dirAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $dirAcl.SetOwner((New-Object System.Security.Principal.NTAccount 'NT AUTHORITY\SYSTEM'))
    $dirAcl.SetAccessRuleProtection($true, $false)
    $dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $systemSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    $dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $adminSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    $dirAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $kioskSid, 'ReadAndExecute,ListDirectory,Read', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    $dir.SetAccessControl($dirAcl)
}

Export-ModuleMember -Function Set-KioskTrustFileAcl