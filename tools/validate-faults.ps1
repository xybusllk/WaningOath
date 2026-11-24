# ============================================================
#  WaningOath - M3 validator fault-injection test
#
#  WHY THIS EXISTS
#  A validator that reports "0 errors" proves nothing on its own:
#  it could be reporting 0 because it never actually looks at the
#  data. This script copies data/ to a scratch tree, injects one
#  deliberate fault per check, and asserts the validator flags the
#  expected check id for every one of them.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\validate-faults.ps1
#  Exit code 0 = every injected fault was caught.
#
#  IMPORTANT: this file is deliberately ASCII-only.
#  Windows PowerShell reads a .ps1 without a BOM as ANSI, so any CJK
#  literal here would come back as mojibake and silently never match.
#  All assertions go through the JSON report emitted by `validate --json`.
# ============================================================

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $repo 'data'
$dst  = Join-Path $repo 'sim-out\_faults\data'

if (-not (Test-Path $src)) { Write-Host "FAIL: $src not found"; exit 2 }

# 1. Fresh copy of the data tree.
if (Test-Path (Split-Path -Parent $dst)) { Remove-Item -Recurse -Force (Split-Path -Parent $dst) }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
Copy-Item -Recurse -Force $src $dst

function Edit-Json([string]$rel, [string]$pattern, [string]$replacement) {
    $p = Join-Path $dst $rel
    $t = [System.IO.File]::ReadAllText($p)
    if ($t -notmatch $pattern) { throw "pattern not found in $rel : $pattern" }
    $t2 = [regex]::Replace($t, $pattern, $replacement)
    if ($t2 -eq $t) { throw "replacement was a no-op in $rel : $pattern" }
    [System.IO.File]::WriteAllText($p, $t2, (New-Object System.Text.UTF8Encoding($false)))
}

function Insert-AfterBrace([string]$rel, [string]$text) {
    $p = Join-Path $dst $rel
    $t = [System.IO.File]::ReadAllText($p)
    $i = $t.IndexOf('{')
    if ($i -lt 0) { throw "no opening brace in $rel" }
    $t2 = $t.Substring(0, $i + 1) + "`r`n  " + $text + $t.Substring($i + 1)
    [System.IO.File]::WriteAllText($p, $t2, (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------
#  One fault per check.
# ---------------------------------------------------------------
$faults = @(
    @{ Check = '1';         Name = 'id does not match file name';
       Do = { Edit-Json 'characters\char-shouyueren.json' '"id"\s*:\s*"char-shouyueren"' '"id": "char-wrongname"' } }

    @{ Check = '2';         Name = 'element is not a registered element';
       Do = { Edit-Json 'characters\char-belrand.json' '"element"\s*:\s*"fire"' '"element": "plasma"' } }

    @{ Check = '5';         Name = 'skill scalingBase is neither atk nor hp';
       Do = { Edit-Json 'characters\char-belrand.json' '"scalingBase"\s*:\s*"atk"' '"scalingBase": "def"' } }

    @{ Check = '8';         Name = '5-star weapon missing its sub stat';
       Do = { Edit-Json 'weapons\weap-hengdao-yuanchu.json' '"subStat"\s*:\s*\{' '"subStat": null, "_removed": {' } }

    @{ Check = '9';         Name = 'eidolon raises the skill cap above 14';
       Do = { Edit-Json 'characters\char-shouyueren.json' '"cap"\s*:\s*14' '"cap": 15' } }

    @{ Check = '17';        Name = 'stackCounter uses an unregistered counter';
       Do = { Edit-Json 'characters\char-belrand.json' '"stackCounter"\s*:\s*"perHit"' '"stackCounter": "perRound"' } }

    @{ Check = 'M3-category'; Name = 'enemy category is unregistered';
       Do = { Edit-Json 'enemies\anom-ccrf-509.json' '"category"\s*:\s*"anomaly"' '"category": "robot"' } }

    @{ Check = 'M3-stage';  Name = 'stage references an enemy that does not exist';
       Do = { Edit-Json 'stages\stage-sim-boss.json' '"anom-ccrf-411"' '"anom-does-not-exist"' } }

    @{ Check = 'M3-tiers';  Name = 'character anchorRow is not a tier row';
       Do = { Edit-Json 'characters\char-alice.json' '"anchorRow"\s*:\s*"hpDps"' '"anchorRow": "noSuchRow"' } }

    @{ Check = '10';        Name = 'skill applies a buff id that is never defined';
       Do = { Edit-Json 'characters\char-qianyu-xingyao.json' '"buffId"\s*:\s*"buff-starlink"' '"buffId": "buff-nope"' } }

    @{ Check = 'M3-keys';   Name = 'JSON key that is silently ignored by the DTO';
       Do = { Insert-AfterBrace 'effects\debuff-blur.json' '"bogusKeyThatNobodyReads": 1,' } }
)

Write-Host "Injecting $($faults.Count) faults into $dst ..."
foreach ($f in $faults) {
    & $f.Do
    Write-Host ("  injected {0,-12} {1}" -f $f.Check, $f.Name)
}
Write-Host ''

$sim = Join-Path $PSScriptRoot 'run-sim.cmd'

# 2. Run the validator against the broken tree and read its JSON report.
$badJson = Join-Path $repo 'sim-out\validate-faults.json'
$badLog  = Join-Path $repo 'sim-out\validate-faults.txt'
& cmd /c "`"$sim`" validate --data `"$dst`" --json `"$badJson`" --quiet > `"$badLog`" 2>&1" | Out-Null

if (-not (Test-Path $badJson)) { Write-Host "FAIL: validator produced no JSON report at $badJson"; exit 2 }
$report = Get-Content $badJson -Raw -Encoding UTF8 | ConvertFrom-Json
$found = @{}
foreach ($i in $report.issues) { $found[$i.check] = $true }

# 3. Every expected check must have fired.
$missing = @()
Write-Host '---- injected-fault detection ----'
foreach ($f in $faults) {
    $hit = $found.ContainsKey($f.Check)
    if (-not $hit) { $missing += "$($f.Check) [$($f.Name)]" }
    Write-Host ("  {0}  {1,-12} {2}" -f $(if ($hit) { 'CAUGHT' } else { 'MISSED' }), $f.Check, $f.Name)
}
Write-Host ''
Write-Host ("broken tree: {0} errors / {1} warnings / {2} infos" -f $report.errors, $report.warnings, $report.infos)
Write-Host "full report: $badJson"

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host "M3 fault injection: FAIL - $($missing.Count) fault(s) went undetected:"
    foreach ($m in $missing) { Write-Host "  - $m" }
    exit 1
}

# 4. The clean tree must still be 0 errors (guards against "reports errors on everything").
$cleanJson = Join-Path $repo 'sim-out\validate-clean.json'
& cmd /c "`"$sim`" validate --json `"$cleanJson`" --quiet > nul 2>&1" | Out-Null
$clean = Get-Content $cleanJson -Raw -Encoding UTF8 | ConvertFrom-Json
if ($clean.errors -ne 0) {
    Write-Host "M3 fault injection: FAIL - the clean data tree reports $($clean.errors) errors"
    exit 1
}

Write-Host ''
Write-Host "M3 fault injection: PASS - all $($faults.Count) injected faults were caught, clean tree still 0 errors."
exit 0
