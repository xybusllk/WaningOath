# assets-check.ps1 -- verify client-referenced image files exist and are real images.
#
# Why this exists: the client loads art by FILE NAME STRING
# (WaningOath/Scripts/Portraits.cs + Backgrounds.cs + GameRoot.cs), and a typo
# there fails SILENTLY -- TryLoad returns null, the UI just drops that layer, and
# nothing anywhere goes red. That is exactly the class of bug this project keeps
# getting bitten by, so it gets a machine check.
#
# Covered now (the art plan asked for the background half explicitly, 02 sec.7 R5):
#   * Assets/Portraits/    portraits + outfits   (png)
#   * Assets/Backgrounds/  scene backgrounds     (png)
#
# ASCII ONLY on purpose: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a CJK
# literal in this file would be mangled into garbage. Same rule as tools/build.cmd.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptDir = Join-Path $root 'WaningOath\Scripts'
$dirs = @(
    (Join-Path $root 'WaningOath\Assets\Portraits'),
    (Join-Path $root 'WaningOath\Assets\Backgrounds')
)

if (-not (Test-Path $scriptDir)) { Write-Host "FAIL: missing $scriptDir"; exit 1 }
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) { Write-Host "FAIL: missing $d"; exit 1 }
}

# 1) every "xxx.png" / "xxx.jpg" string literal in the client scripts must resolve.
$names = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem $scriptDir -Filter *.cs | ForEach-Object {
    $text = Get-Content $_.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, '"([A-Za-z0-9_\-]+\.(?:png|jpg))"')) {
        [void]$names.Add($m.Groups[1].Value)
    }
}

if ($names.Count -eq 0) { Write-Host 'FAIL: no image references found in the client scripts'; exit 1 }

function Test-Signature([string]$path, [string]$ext) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 1024) { return "too small ($($bytes.Length) bytes)" }
    if ($ext -eq 'png') {
        # PNG magic: 89 50 4E 47 0D 0A 1A 0A
        if (-not ($bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47)) {
            return 'no PNG signature'
        }
        return ''
    }
    # JPEG SOI marker: FF D8 FF
    if (-not ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8 -and $bytes[2] -eq 0xFF)) { return 'no JPEG SOI marker' }
    return ''
}

$missing = @()
$bad = @()
$portraits = 0
$backgrounds = 0
$bgMissing = @()

foreach ($n in ($names | Sort-Object)) {
    $found = $null
    foreach ($d in $dirs) {
        $p = Join-Path $d $n
        if (Test-Path $p) { $found = $p; break }
    }
    if (-not $found) {
        # Backgrounds are a GREENFIELD: the art source folder (scene images, CJK
        # names) is still empty, so "not drawn yet" and "typo in the file name"
        # look the same. Missing backgrounds are therefore a WARNING that names
        # the exact file the client will look for -- which is also the brief for
        # whoever draws it. Portraits are all delivered, so those stay fatal.
        if ($n -like 'shouyueren-*' -or $n -like 'xingyao-*') { $missing += $n } else { $bgMissing += $n }
        continue
    }

    if ($found -like '*\Portraits\*') { $portraits++ } else { $backgrounds++ }

    $ext = [System.IO.Path]::GetExtension($n).TrimStart('.').ToLowerInvariant()
    $why = Test-Signature $found $ext
    if ($why -ne '') { $bad += "$n ($why)" }
}

Write-Host "image references: $($names.Count)  (portraits delivered $portraits, backgrounds delivered $backgrounds)"
foreach ($n in ($names | Sort-Object)) { Write-Host "  $n" }

if ($bgMissing.Count -gt 0) {
    Write-Host ""
    Write-Host "backgrounds still to be drawn (NOT a failure - the client soft-lands):"
    foreach ($n in $bgMissing) { Write-Host "  $n" }
}
if ($missing.Count -gt 0) { Write-Host "MISSING (fatal): $($missing -join ', ')" }
if ($bad.Count -gt 0) { Write-Host "NOT A REAL IMAGE: $($bad -join ', ')" }

if ($missing.Count -gt 0 -or $bad.Count -gt 0) {
    Write-Host 'assets check: FAIL'
    exit 1
}

Write-Host ''
Write-Host ("assets check: PASS - every delivered image exists and carries a valid signature" +
            " (backgrounds delivered: $backgrounds).")
exit 0
