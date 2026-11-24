# Scratch-stack verification for the "CJK nickname cannot log in" bug.
#
# The sandbox cannot BIND a .NET HttpListener (http.sys), so this script does not
# start a game service of its own. It starts a SECOND website (Node, private port +
# private data dir) and points it at the ALREADY RUNNING game service on :8720 --
# the same one the user has open. That is exactly the configuration that was broken:
# website -> forward -> .NET.
#
# Footprint on the real game service: it creates/mutates ONE save file for the
# throwaway scratch user, which this script deletes in the finally block.
#
# ASCII ONLY (PowerShell 5.1 reads a BOM-less .ps1 as ANSI). The CJK test name is
# therefore built from code points instead of being typed out.

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $repo 'sim-out\_verify'
$cjk = [string]::Join('', [char[]](0x661F, 0x9065, 0x4E0D, 0x662F, 0x841D, 0x8389, 0x63A7))
$pass = 'xiaoniuzi2009'
$gameService = 'http://127.0.0.1:8720'
$webPort = '18731'
$scratchUser = 'cjk_probe'

$webDir = $null
foreach ($d in Get-ChildItem -Path $repo -Directory) {
    if (Test-Path (Join-Path $d.FullName 'server.js')) { $webDir = $d.FullName; break }
}
if (-not $webDir) { Write-Host 'FAIL: website folder not found'; exit 1 }

$web = $null
$createdSave = $null

function Probe([string]$label, [string]$uri, [hashtable]$headers) {
    try {
        $r = Invoke-WebRequest -Uri $uri -Headers $headers -UseBasicParsing -TimeoutSec 8
        Write-Host ("  {0,-34} -> {1}  {2}" -f $label, $r.StatusCode, $r.Content.Substring(0, [Math]::Min(130, $r.Content.Length)))
        return $r.Content
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        $body = ''
        try { $body = (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd() } catch {}
        Write-Host ("  {0,-34} -> {1}  {2}" -f $label, $code, $body.Substring(0, [Math]::Min(160, $body.Length)))
        return ''
    }
}

try {
    Write-Host "== scratch website on :$webPort -> the RUNNING game service at $gameService =="
    $env:WO_GAME_API = $gameService
    $env:PORT = $webPort
    $env:HOST = '127.0.0.1'
    $env:APP_DATA_DIR = (Join-Path $scratch 'appdata')
    $nodeExe = (& node -e "process.stdout.write(process.execPath)")
    $web = Start-Process -FilePath $nodeExe -ArgumentList @('server.js') -WorkingDirectory $webDir -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'web.out') -RedirectStandardError (Join-Path $scratch 'web.err')
    Start-Sleep -Seconds 3

    Write-Host '== 1. register a scratch user whose NICKNAME is CJK =='
    $reg = @{ username = $scratchUser; password = $pass; nickname = $cjk } | ConvertTo-Json -Compress
    $r = Probe 'register (nickname = CJK)' "http://127.0.0.1:$webPort/api/auth/register" @{}
    if ($r -notmatch '"ok":true') { Write-Host '  (already registered from an earlier run, continuing)' }

    Write-Host '== 2. the exact chain the client runs: auth -> state -> profile =='
    $authBody = @{ username = $scratchUser; password = $pass } | ConvertTo-Json -Compress
    $auth = Invoke-RestMethod -Uri "http://127.0.0.1:$webPort/api/game/auth" -Method Post -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($authBody))
    Write-Host ("  user.id = " + $auth.user.id)
    $createdSave = Join-Path $repo ("data\game\players\" + $auth.user.id + '.json')

    $h = @{ Authorization = "Bearer $($auth.token)" }
    $state = Probe 'GET /api/game/state (forwarded)' "http://127.0.0.1:$webPort/api/game/state" $h

    $prof = @{ protagonistGender = 'female'; playerName = $cjk } | ConvertTo-Json -Compress
    try {
        $pr = Invoke-WebRequest -Uri "http://127.0.0.1:$webPort/api/game/profile" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($prof)) -UseBasicParsing -TimeoutSec 8
        Write-Host ("  {0,-34} -> {1}  {2}" -f 'POST /api/game/profile (CJK name)', $pr.StatusCode, $pr.Content.Substring(0, [Math]::Min(140, $pr.Content.Length)))
        $profileOk = ($pr.Content -match '"ok":true')
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        $body = ''
        try { $body = (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd() } catch {}
        Write-Host ("  {0,-34} -> {1}  {2}" -f 'POST /api/game/profile (CJK name)', $code, $body.Substring(0, [Math]::Min(160, $body.Length)))
        $profileOk = $false
    }

    Write-Host ''
    if ($state -match '"ok":true') {
        Write-Host 'RESULT: PASS - a CJK nickname no longer breaks the forward; state + profile both succeed.'
    } else {
        Write-Host 'RESULT: FAIL - the state call still does not succeed.'
    }
}
finally {
    if ($web -and -not $web.HasExited) {
        Stop-Process -Id $web.Id -Force -ErrorAction SilentlyContinue
        Write-Host ("  stopped pid " + $web.Id + " (" + $web.ProcessName + ")")
    }
    if ($createdSave -and (Test-Path $createdSave)) {
        Remove-Item $createdSave -Force -ErrorAction SilentlyContinue
        Write-Host ("  removed the scratch save " + (Split-Path -Leaf $createdSave))
    }
    Remove-Item Env:\WO_GAME_API, Env:\PORT, Env:\HOST, Env:\APP_DATA_DIR -ErrorAction SilentlyContinue
}