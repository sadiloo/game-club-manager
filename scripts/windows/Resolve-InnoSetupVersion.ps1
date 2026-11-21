function Try-ParseInnoVersionString([string]$text) {
  if ([string]::IsNullOrWhiteSpace($text)) {
    return $null
  }
  if ($text -eq "0.0.0.0") {
    return $null
  }
  try {
    return [Version]$text
  }
  catch {
    return $null
  }
}

function Test-VersionMatchesExpected([Version]$parsed, [Version]$expected) {
  if ($null -eq $parsed) {
    return $false
  }
  return $parsed.Major -eq $expected.Major `
    -and $parsed.Minor -eq $expected.Minor `
    -and $parsed.Build -eq $expected.Build
}

function Parse-InnoBannerCompilerVersion([string]$bannerText) {
  if ([string]::IsNullOrWhiteSpace($bannerText)) {
    return $null
  }
  if ($bannerText -match "(?m)Compiler engine version:\s*Inno Setup\s+(\d+\.\d+(?:\.\d+)?)") {
    return $Matches[1]
  }
  if ($bannerText -match "(?m)Compiler version\s+(\d+\.\d+(?:\.\d+)?)") {
    return $Matches[1]
  }
  return $null
}

function Resolve-InnoVersionDecision {
  param(
    [string]$DisplayVersion,
    [string]$FileVersion,
    [string]$ProductVersion,
    [string]$BannerText,
    [Version]$Expected
  )

  $displayParsed = Try-ParseInnoVersionString $DisplayVersion
  $fileParsed = Try-ParseInnoVersionString $FileVersion
  $productParsed = Try-ParseInnoVersionString $ProductVersion
  $bannerVersionText = Parse-InnoBannerCompilerVersion $BannerText
  $bannerParsed = Try-ParseInnoVersionString $bannerVersionText

  $contradictions = [System.Collections.Generic.List[string]]::new()

  if ($null -eq $displayParsed -or -not (Test-VersionMatchesExpected $displayParsed $Expected)) {
    return @{
      Ok               = $false
      Reason           = "DisplayVersion must be $Expected (registry uninstall key)."
      DisplayVersion   = $DisplayVersion
      FileVersion      = $FileVersion
      ProductVersion   = $ProductVersion
      BannerVersion    = $bannerVersionText
      Contradictions   = @()
    }
  }

  if ($null -eq $bannerParsed -or -not (Test-VersionMatchesExpected $bannerParsed $Expected)) {
    return @{
      Ok               = $false
      Reason           = "Compiler engine version banner must match $Expected."
      DisplayVersion   = $DisplayVersion
      FileVersion      = $FileVersion
      ProductVersion   = $ProductVersion
      BannerVersion    = $bannerVersionText
      Contradictions   = @()
    }
  }

  foreach ($entry in @(
      @{ Name = "FileVersion"; Parsed = $fileParsed; Raw = $FileVersion }
      @{ Name = "ProductVersion"; Parsed = $productParsed; Raw = $ProductVersion }
    )) {
    if ($null -eq $entry.Parsed) {
      continue
    }
    if (-not (Test-VersionMatchesExpected $entry.Parsed $Expected)) {
      $contradictions.Add("$($entry.Name)=$($entry.Raw)")
    }
  }

  if ($contradictions.Count -gt 0) {
    return @{
      Ok               = $false
      Reason           = "version source(s) contradict DisplayVersion $Expected."
      DisplayVersion   = $DisplayVersion
      FileVersion      = $FileVersion
      ProductVersion   = $ProductVersion
      BannerVersion    = $bannerVersionText
      Contradictions   = $contradictions.ToArray()
    }
  }

  return @{
    Ok               = $true
    Reason           = $null
    DisplayVersion   = $DisplayVersion
    FileVersion      = $FileVersion
    ProductVersion   = $ProductVersion
    BannerVersion    = $bannerVersionText
    Contradictions   = @()
  }
}