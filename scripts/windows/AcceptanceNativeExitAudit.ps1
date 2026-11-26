# R10g item 4 (rr5 L11b): a native failure followed by a native success must still fail an acceptance leg. $LASTEXITCODE
# only remembers the LAST native, so the ledger cannot see an earlier unchecked failure at run time. Instead every native
# call in a leg script (and in the acceptance scripts it dot-sources from its own folder) must disposition its exit code
# before the next native call: read $LASTEXITCODE (checked), or carry a `# native-exit: ignored <reason>` annotation on
# the same line or the line above (deliberate best effort, e.g. diagnostics or deleting a service that may not exist).
# ci-installer-acceptance.ps1 refuses to run a leg with an undispositioned native call (phase native-audit).

function Test-AcceptanceNativeCommandAst([System.Management.Automation.Language.CommandAst] $Command) {
    $first = $Command.CommandElements[0]
    if ($Command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot) { return $false }
    if ($Command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand) {
        if ($first -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { return $false }
        # Script blocks handed to helpers (Write-AcceptanceHarnessTrace, Invoke-With...) are not native commands.
        if ($first -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $first.VariablePath.UserPath -match '^(Action|ScriptBlock|Log|Transform|DriverTransform|Body|Block)$') { return $false }
        if ($first.Extent.Text -match '\.ps1\b') { return $false }
        return $true
    }
    $name = $Command.GetCommandName()
    if (-not $name) { return $false }
    if ($name -match '\.ps1$') { return $false }
    if ($name -match '\.(exe|cmd|bat|com)$') { return $true }
    $builtin = @('foreach', 'where', 'sort', 'select', 'echo', 'sleep', 'cd', 'ls', 'dir', 'cat', 'rm', 'del', 'copy', 'move', 'ni', 'gc', 'gi', 'iex', 'iwr', 'irm')
    return ($name -notmatch '-' -and $builtin -notcontains $name.ToLowerInvariant())
}

function Get-AcceptanceDotSourcedScripts([string] $Path) {
    # The leg script plus every script it dot-sources from its own folder via (Join-Path $PSScriptRoot "X.ps1"), transitively.
    $seen = [System.Collections.Generic.List[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue([IO.Path]::GetFullPath($Path))
    while ($queue.Count -gt 0) {
        $p = $queue.Dequeue()
        if ($seen -contains $p -or -not (Test-Path -LiteralPath $p)) { continue }
        $seen.Add($p)
        $src = [IO.File]::ReadAllText($p)
        foreach ($m in [regex]::Matches($src, '(?m)^\s*\.\s+\(Join-Path\s+\$PSScriptRoot\s+"([^"\\/]+\.ps1)"\)')) {
            $queue.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path $p -Parent) $m.Groups[1].Value)))
        }
    }
    return $seen.ToArray()
}

function Get-AcceptanceUncheckedNativeCalls([string] $Path) {
    $tokens = $null; $errs = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errs)
    if ($errs.Count -gt 0) { return @("$(Split-Path $Path -Leaf): does not parse: $($errs[0].Message)") }
    $text = [IO.File]::ReadAllText($Path)
    $lines = $text -split "`r?`n"
    $natives = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] -and (Test-AcceptanceNativeCommandAst $a) }, $true) |
        Sort-Object { $_.Extent.StartOffset })
    # A read of $LASTEXITCODE (anything but the left side of an assignment) dispositions the native before it.
    $reads = @($ast.FindAll({ param($a)
                $a -is [System.Management.Automation.Language.VariableExpressionAst] -and $a.VariablePath.UserPath -match '^(global:)?LASTEXITCODE$' -and
                -not ($a.Parent -is [System.Management.Automation.Language.AssignmentStatementAst] -and [object]::ReferenceEquals($a.Parent.Left, $a)) }, $true) |
        ForEach-Object { $_.Extent.StartOffset })
    $found = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $natives.Count; $i++) {
        $n = $natives[$i]
        $from = $n.Extent.EndOffset
        $to = if ($i + 1 -lt $natives.Count) { $natives[$i + 1].Extent.StartOffset } else { $text.Length }
        $fn = $n.Parent
        while ($fn -and $fn -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $fn = $fn.Parent }
        if ($fn -and $fn.Extent.EndOffset -lt $to) { $to = $fn.Extent.EndOffset }
        if (@($reads | Where-Object { $_ -ge $from -and $_ -lt $to }).Count -gt 0) { continue }
        $ln = $n.Extent.StartLineNumber
        $annotation = '#\s*native-exit:\s*ignored\s+\S'
        if ($lines[$ln - 1] -match $annotation -or ($ln -ge 2 -and $lines[$ln - 2] -match "^\s*$annotation")) { continue }
        $found.Add(("{0}:{1}: {2}" -f (Split-Path $Path -Leaf), $ln, ($n.Extent.Text -replace '\s+', ' ')))
    }
    return $found.ToArray()
}
