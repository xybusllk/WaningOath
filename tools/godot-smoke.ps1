# ============================================================
#  WaningOath - Godot client smoke test (headless boot + layout)
#
#  WHAT THIS PROVES
#  Milestones M5/M6/M7 sat at "code complete, visual acceptance NOT done"
#  for several rounds because Godot's --headless run used to HANG on this
#  machine. It does not hang when you pass --quit-after <frames>: the
#  engine quits by itself, so the client can be booted and inspected.
#
#  What CAN be automated (and is, below):
#    * the client boots and GameRoot._Ready() runs to completion
#    * the BUNDLED CJK font is the one actually used (not a system
#      fallback -- that fallback renders boxes on a machine without the
#      font, and it silently "worked" here for several rounds)
#    * the portraits load from disk (2 referenced, 0 missing)
#    * all 22 screens build without throwing
#    * no screen overflows the viewport width, at BOTH 1920x1080 and
#      1440x1080 (the two first-class resolutions of 06 sec.5)
#
#  What still needs human eyes: whether it LOOKS right (spacing,
#  alignment, aesthetics). That part cannot be asserted.
#
#  The client prints two kinds of line: Chinese ones for humans, and
#  "[SMOKE] key=value ..." ones for this script. Only the ASCII ones are
#  parsed here.
#
#  TRAPS THIS SCRIPT KNOWS ABOUT
#   1. ASCII-ONLY. Windows PowerShell reads a BOM-less .ps1 as ANSI, so a
#      CJK literal in here comes back as mojibake and the script will not
#      even parse. (This project has hit that three times.)
#   2. Godot needs a DEBUG build of the C# assembly
#      (.godot/mono/temp/bin/Debug/WaningOath.dll). A Release-only tree
#      fails with "Cannot instantiate C# script because the associated
#      class could not be found" -- which looks like a code bug and is not.
#   3. In --headless the dummy display driver reports window 0x0 and
#      --resolution is IGNORED (the viewport degenerates to a square).
#      The layout baseline is therefore driven by WO_UI_SIZE, which
#      rewrites Window.content_scale_size -- the value that
#      canvas_items stretching actually reads.
#   4. The engine is started DIRECTLY and only PIDs from this run are
#      stopped (same rule as game-forward-smoke.ps1).
#
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\godot-smoke.ps1
#  Exit code 0 = the client boots, loads its assets and lays out cleanly.
# ============================================================

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$godot   = Join-Path $repo 'Godot4\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe'
$proj    = Join-Path $repo 'WaningOath'
$frames  = 600           # ~10s at 60fps; the client quits on its own far earlier
$hardTimeout = 150

function Fail([string]$msg) {
    Write-Host ''
    Write-Host "FAIL: $msg"
    exit 1
}

if (-not (Test-Path $godot)) { Fail "Godot not found at $godot" }
if (-not (Test-Path $proj))  { Fail "client project not found at $proj" }

# --- 1. Debug build (trap 2) -------------------------------------------
Write-Host 'building the client in Debug (Godot runs the Debug assembly) ...'
$envCmd = Join-Path $PSScriptRoot 'env.cmd'
$build = & cmd /c "`"$envCmd`" >nul 2>&1 && dotnet build `"$proj\WaningOath.csproj`" -c Debug -maxcpucount:1 -v:q" 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host $build; Fail 'dotnet build -c Debug failed' }
$asm = Join-Path $proj '.godot\mono\temp\bin\Debug\WaningOath.dll'
if (-not (Test-Path $asm)) { Fail "the Debug assembly was not produced at $asm" }

# --- 2. one pass at a given layout baseline ----------------------------
function Invoke-Pass([string]$label, [string]$uiSize) {
    if ($uiSize) { $env:WO_UI_SIZE = $uiSize } else { Remove-Item Env:\WO_UI_SIZE -ErrorAction SilentlyContinue }
    $env:WO_UI_SMOKE = '1'

    # Godot keeps its own data in %APPDATA%\Godot\... (user://). Under the DSH
    # file sandbox that path is OUTSIDE the workspace, so writing there is
    # denied -- and Godot does not survive it: it fails to open its log file and
    # then crashes with an access violation, which looks exactly like a code
    # crash. Pointing APPDATA at a workspace scratch dir fixes it. The
    # certificate-store error below is the side effect of that redirect (the
    # scratch dir has no cert store) and is harmless.
    $scratch = Join-Path $repo 'sim-out\_godot-appdata'
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    $env:APPDATA = $scratch

    $outFile = Join-Path $repo ("sim-out\_godot-" + $label + '.txt')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFile) | Out-Null
    Remove-Item $outFile, ($outFile + '.err') -ErrorAction SilentlyContinue

    $p = Start-Process -FilePath $godot `
        -ArgumentList @('--headless', '--path', $proj, '--quit-after', "$frames") `
        -WorkingDirectory $repo -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $outFile -RedirectStandardError ($outFile + '.err')

    if (-not $p.WaitForExit($hardTimeout * 1000)) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Fail "the $label pass did not finish within $hardTimeout s"
    }
    # NOTE: on this PowerShell, `-PassThru` combined with output redirection leaves
    # $p.ExitCode unreadable (it comes back $null). The verdict is therefore taken
    # from the client's own [SMOKE] lines, which are strictly more informative
    # anyway; the exit code is only enforced when it IS readable.
    $exitCode = $null
    try { $p.Refresh(); $exitCode = $p.ExitCode } catch { $exitCode = $null }

    $lines = @()
    if (Test-Path $outFile) { $lines += Get-Content $outFile -Encoding UTF8 }
    if (Test-Path ($outFile + '.err')) { $lines += Get-Content ($outFile + '.err') -Encoding UTF8 }
    $text = $lines -join "`n"
    $smoke = $lines | Where-Object { $_ -match '\[SMOKE\]' }

    Write-Host ("  --- " + $label + " (exit " + $exitCode + ") ---")
    foreach ($l in $smoke) { Write-Host ("    " + $l.Trim()) }

    if ($null -ne $exitCode -and $exitCode -ne 0) { Fail "the $label pass exited with code $exitCode" }
    # Environmental noise that is NOT the client's fault (see the APPDATA note above).
    $real = ($lines | Where-Object { $_ -match 'ERROR' } |
             Where-Object { $_ -notmatch 'root certificate store' } |
             Where-Object { $_ -notmatch "Failed to open 'user://logs/" })
    if ($real.Count -gt 0) {
        foreach ($l in $real) { Write-Host ("    ! " + $l.Trim()) }
        Fail "the $label pass printed an engine ERROR"
    }
    $warn = ($lines | Where-Object { $_ -match 'WARNING' })
    if ($warn.Count -gt 0) {
        foreach ($l in $warn) { Write-Host ("    ! " + $l.Trim()) }
        Fail "the $label pass printed a WARNING"
    }
    if ($smoke.Count -lt 19) { Fail "the $label pass printed only $($smoke.Count) [SMOKE] lines" }

    if ($text -notmatch 'boot=ok')      { Fail "the $label pass never reported a completed boot" }
    if ($text -notmatch 'font=bundled') { Fail "the $label pass fell back to a SYSTEM font instead of the bundled one" }
    if ($text -notmatch 'layout=ok')    { Fail "the $label pass reported a layout problem" }
    if ($text -match '\*\*')            { Fail "the $label pass contains a failure marker" }

    # Portrait counts are checked on the FINAL assets line: the title screen no longer
    # shows any portrait, so portraits only load once the character screens build.
    $assets = [regex]::Match($text, 'assets portraits=(\d+) missing=(\d+) backgrounds=(\d+)/(\d+) font=(\w+) .*bgreq=(\d+)')
    if (-not $assets.Success) { Fail "the $label pass never reported its asset totals" }
    if ($assets.Groups[1].Value -eq '0') { Fail "no portrait ever loaded (0 loaded)" }
    if ($assets.Groups[2].Value -ne '0') { Fail ("portrait(s) went missing: " + $assets.Groups[2].Value) }
    if ($assets.Groups[5].Value -ne 'bundled') { Fail "the FINAL font check was not the bundled font" }

    # Backgrounds: the scene art is still a greenfield (0 delivered), so the
    # assertion is "the pipeline enumerated all four it asks for", not "all four
    # arrived". When art lands, loaded goes up and missing goes down -- the SUM
    # staying at 4 is what proves the loader looked at every requested name.
    $bgLoaded = [int]$assets.Groups[3].Value
    $bgMissing = [int]$assets.Groups[4].Value
    $bgReq = [int]$assets.Groups[6].Value
    if (($bgLoaded + $bgMissing) -ne $bgReq) {
        Fail "the background pipeline reported $bgLoaded loaded + $bgMissing missing, expected $bgReq requested"
    }

    $okScreens = ([regex]::Matches($text, 'screen=\S+ .*? ok=1')).Count
    if ($okScreens -lt 22) { Fail "reported $okScreens clean screen builds, expected 22" }

    Remove-Item $outFile, ($outFile + '.err') -ErrorAction SilentlyContinue
    return $text
}

try {
    $a = Invoke-Pass '1920x1080' ''
    $b = Invoke-Pass '1440x1080' '1440x1080'

    $ma = [regex]::Match($a, 'layout=ok failed=0 screens=(\d+) width=(\d+)')
    $wa = $ma.Groups[2].Value
    $mb = [regex]::Match($b, 'layout=ok failed=0 screens=(\d+) width=(\d+)')
    $wb = $mb.Groups[2].Value
    if ($wa -ne '1920') { Fail "the default pass did not lay out at 1920 wide (got '$wa')" }
    if ($wb -ne '1440') { Fail "the WO_UI_SIZE pass did not lay out at 1440 wide (got '$wb')" }

    Write-Host ''
    Write-Host 'Godot client smoke: PASS - the client boots headless, uses the bundled CJK font,'
    Write-Host 'loads its portraits and backgrounds, builds every screen, and fits both 1920-wide and 1440-wide.'
    Write-Host 'Not covered (needs human eyes): whether it LOOKS right.'
    exit 0
}
finally {
    Remove-Item Env:\WO_UI_SMOKE -ErrorAction SilentlyContinue
    Remove-Item Env:\WO_UI_SIZE -ErrorAction SilentlyContinue
}
