# ============================================================
#  WaningOath - website -> game-service forwarding smoke test
#
#  WHAT THIS PROVES
#  06 sec.2.0 says the website (Node) forwards /api/game/* to the .NET
#  game service and injects the identity headers, so that
#  /api/game/auth and /api/game/auth/verify stay on the Node side.
#  The forwarding is not merely "documented", it is *verified* here:
#
#    * GET  /api/game/health -> 200 without a session (comes from .NET)
#    * GET  /api/game/stages -> 401 WITHOUT a session (the identity header
#      is derived from the server-side session; a client cannot forge it)
#    * GET  /api/game/stages -> 200 WITH a session, and the body is the .NET
#      shape (the Node side has no such route at all, so a 404 here would
#      mean "not forwarded")
#    * POST /api/game/auth  -> stays on Node and mints the token
#    * a client-supplied X-WO-User header is ignored
#
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\game-forward-smoke.ps1
#  Exit code 0 = forwarding verified.
#
#  TWO TRAPS THIS SCRIPT ALREADY FELL INTO (kept here so it does not again):
#
#   1. THIS FILE MUST STAY ASCII-ONLY. Windows PowerShell reads a BOM-less
#      .ps1 as ANSI, so a CJK literal here comes back as mojibake. The website
#      folder is therefore *discovered* (the folder containing server.js)
#      instead of being written out by name.
#   2. The website resolves its data dir from APP_DATA_DIR. Copying db.json
#      aside does nothing -- an earlier version of this test registered its
#      throwaway account into the REAL website database.
# ============================================================

$ErrorActionPreference = 'Stop'

# 任何一个请求挂住都不能让脚本无限等下去：默认 20 秒超时。
$PSDefaultParameterValues['Invoke-RestMethod:TimeoutSec'] = 20

$repo    = Split-Path -Parent $PSScriptRoot
$apiPort = 18721
$webPort = 18080
$user    = 'fwd_smoke_' + (Get-Random -Maximum 999999)
$pass    = 'Smoke!' + (Get-Random -Maximum 999999)
$scratch = Join-Path $repo 'sim-out\_forward'
$api = $null
$web = $null

function Fail([string]$msg) {
    Write-Host ''
    Write-Host "FAIL: $msg"
    exit 1
}

function Cleanup {
    # Only the two PIDs *this script started* are stopped, and both are started
    # DIRECTLY (no cmd.exe / dotnet-run wrapper, and with the *real* node.exe)
    # so "what I started" and "what I stop" are the same set. An earlier version
    # wrapped the website in cmd.exe and stopped the wrapper instead of node,
    # leaving a server alive on the test port; the next run then silently talked
    # to that stale server and proved nothing.
    foreach ($p in @($web, $api)) {
        if ($p -and -not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Write-Host ("  stopped pid " + $p.Id + " (" + $p.ProcessName + ")")
        }
    }
    Start-Sleep -Milliseconds 800
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}

# Discover the website folder: the one holding server.js.
# (No CJK literal -- see trap 1 above.)
$webDir = $null
foreach ($d in Get-ChildItem -Path $repo -Directory) {
    if (Test-Path (Join-Path $d.FullName 'server.js')) { $webDir = $d.FullName; break }
}
if (-not $webDir) { Fail "could not find the website folder (one containing server.js) under $repo" }

if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

# Environment for the website child (inherited; no shell wrapper involved).
$env:WO_GAME_API = "http://127.0.0.1:$apiPort"
$env:WO_GAME_GATEWAY_SECRET = 'smoke-secret'
$env:PORT = "$webPort"
$env:HOST = '127.0.0.1'
# trap 2: the website writes here, NOT into its real data folder.
$env:APP_DATA_DIR = Join-Path $scratch 'data'
New-Item -ItemType Directory -Force -Path $env:APP_DATA_DIR | Out-Null

# Run the built game service directly rather than `dotnet run`: `dotnet run`
# spawns the app as a grandchild, which is the same leak in a different coat.
$apiExe = Join-Path $repo 'WaningOath.Server\bin\Release\net9.0\WaningOath.Server.exe'
if (-not (Test-Path $apiExe)) { Fail "build first: $apiExe not found (run tools\build.cmd)" }

# `node` on this machine resolves to a .cmd shim, and Start-Process would return
# the *shim's* pid. Ask node itself where it actually lives.
$nodeExe = (& node -e "process.stdout.write(process.execPath)")
if (-not (Test-Path $nodeExe)) { Fail "could not resolve the real node executable (got '$nodeExe')" }

try {
    Write-Host "starting .NET game service on :$apiPort ..."
    $api = Start-Process -FilePath $apiExe -ArgumentList @('--port', "$apiPort", '--store', (Join-Path $scratch 'players'), '--audit', (Join-Path $scratch 'audit.jsonl'), '--gateway-secret', 'smoke-secret') -WorkingDirectory $repo -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'api.out') -RedirectStandardError (Join-Path $scratch 'api.err')
    Start-Sleep -Seconds 4
    if ($api.HasExited) { Fail ("the game service exited immediately, exit code " + $api.ExitCode) }

    Write-Host "starting the website on :$webPort (WO_GAME_API=http://127.0.0.1:$apiPort) ..."
    $web = Start-Process -FilePath $nodeExe -ArgumentList @('server.js') -WorkingDirectory $webDir -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch 'web.out') -RedirectStandardError (Join-Path $scratch 'web.err')
    Start-Sleep -Seconds 5
    if ($web.HasExited) { Fail ("the website exited immediately, exit code " + $web.ExitCode) }

    $base = "http://127.0.0.1:$webPort"

    $health = Invoke-RestMethod -Uri "$base/api/game/health" -Method Get
    if (-not $health.ok) { Fail 'health did not report ok' }
    if (-not $health.service) { Fail 'health did not come from the .NET service (no service field)' }
    Write-Host ("  CAUGHT health forwarded  -> service=" + $health.service + " stages=" + $health.stages)

    $code = 0
    try { $null = Invoke-RestMethod -Uri "$base/api/game/stages" -Method Get } catch { $code = $_.Exception.Response.StatusCode.value__ }
    if ($code -ne 401) { Fail "without a session /api/game/stages returned $code, expected 401" }
    Write-Host '  CAUGHT no-session request rejected with 401'

    $null = Invoke-RestMethod -Uri "$base/api/auth/register" -Method Post -ContentType 'application/json' -Body (@{ username = $user; password = $pass; nickname = $user } | ConvertTo-Json)
    $auth = Invoke-RestMethod -Uri "$base/api/game/auth" -Method Post -ContentType 'application/json' -Body (@{ username = $user; password = $pass } | ConvertTo-Json)
    if (-not $auth.token) { Fail '/api/game/auth returned no token' }
    Write-Host '  CAUGHT /api/game/auth stayed on Node and minted a token'

    $hdrs = @{ Authorization = "Bearer $($auth.token)" }
    $stages = Invoke-RestMethod -Uri "$base/api/game/stages" -Method Get -Headers $hdrs
    if (-not $stages.count) { Fail '/api/game/stages returned no count (not the .NET payload)' }
    $ids = ($stages.stages | ForEach-Object { $_.id }) -join ','
    Write-Host ("  CAUGHT forwarded stages   -> count=" + $stages.count)
    Write-Host ("         ids: " + $ids)
    if ($ids -notmatch 'stage-sim-normal') { Fail 'the .NET stage whitelist did not come through' }
    if ($ids -notmatch 'stage-exp-1') { Fail 'the exp-farm stages did not come through' }

    $cfg = Invoke-RestMethod -Uri "$base/api/game/config" -Method Get -Headers $hdrs
    if (-not $cfg.levelCap) { Fail '/api/game/config did not return levelCap' }
    Write-Host ("  CAUGHT forwarded config   -> levelCap=" + $cfg.levelCap)

    $forged = @{ Authorization = "Bearer $($auth.token)"; 'X-WO-User' = 'u_someone_else' }
    $state = Invoke-RestMethod -Uri "$base/api/game/state" -Method Get -Headers $forged
    if ($state.save.userId -ne $auth.user.id) {
        Fail ("a forged X-WO-User was honoured: got " + $state.save.userId + ", expected " + $auth.user.id)
    }
    Write-Host '  CAUGHT a forged X-WO-User header is ignored (session wins)'

    # ---------------------------------------------------------------
    #  Admin surface (06 sec.7). These assertions are FIELD-NAME level
    #  on purpose: the admin page (public/admin-game.html) was written
    #  against the Node implementation, and the .NET port used different
    #  names (`current` vs `stamina`, `save` vs `player`, `lines` vs
    #  `entries`) plus three missing routes -- four of six tabs were
    #  silently broken. "Returns 200" would NOT have caught any of it.
    # ---------------------------------------------------------------
    $players = Invoke-RestMethod -Uri "$base/api/game/admin/players" -Method Get -Headers $hdrs
    if (-not $players.players) { Fail 'admin/players returned no players array' }
    $me = $players.players | Where-Object { $_.userId -eq $auth.user.id } | Select-Object -First 1
    if (-not $me) { Fail 'admin/players did not list the smoke account' }
    foreach ($f in @('stamina', 'characterCount', 'totalEidolon', 'protocolCode', 'maxLevel', 'suspicious')) {
        if ($null -eq $me.PSObject.Properties[$f]) { Fail ("admin/players is missing the field the admin page reads: " + $f) }
    }
    Write-Host ("  CAUGHT forwarded admin/players -> " + $players.count + " row(s), page contract intact")

    $detail = Invoke-RestMethod -Uri "$base/api/game/admin/player/$($auth.user.id)" -Method Get -Headers $hdrs
    if (-not $detail.player) { Fail 'admin/player/:id did not return `player` (the admin page reads r.player)' }
    if ($null -eq $detail.PSObject.Properties['suspicious']) { Fail 'admin/player/:id did not return `suspicious`' }
    if (-not $detail.player.characters) { Fail 'admin/player/:id returned a save with no `characters` map' }
    Write-Host '  CAUGHT forwarded admin/player -> player + suspicious'

    $patchBody = @{
        userId = $auth.user.id
        set = @{ protocolLevel = 1; playerName = 'smoke_patched'; stamina = 100 }
    } | ConvertTo-Json -Depth 5
    $patched = Invoke-RestMethod -Uri "$base/api/game/admin/patch" -Method Post -Headers $hdrs -ContentType 'application/json' -Body $patchBody
    if (-not $patched.player) { Fail 'admin/patch did not return `player`' }
    if ($patched.player.profile.playerName -ne 'smoke_patched') { Fail 'admin/patch did not apply playerName' }
    if ($patched.player.stamina.current -ne 100) { Fail 'admin/patch did not apply stamina' }
    Write-Host '  CAUGHT forwarded admin/patch  -> playerName + stamina applied'

    $audit = Invoke-RestMethod -Uri "$base/api/game/admin/audit" -Method Get -Headers $hdrs
    if ($null -eq $audit.entries) { Fail 'admin/audit did not return `entries` (the admin page reads r.entries)' }
    if ($audit.entries.Count -lt 1) { Fail 'admin/audit returned an empty tail right after a patch' }
    if ($null -eq $audit.entries[0].PSObject.Properties['action']) { Fail 'admin/audit entries have no `action` field' }
    if ($null -eq $audit.entries[0].PSObject.Properties['ts']) { Fail 'admin/audit entries have no `ts` field' }
    Write-Host ("  CAUGHT forwarded admin/audit  -> " + $audit.entries.Count + " entries as objects")

    $gcfg = Invoke-RestMethod -Uri "$base/api/game/admin/config" -Method Get -Headers $hdrs
    if ($null -eq $gcfg.gacha) { Fail 'admin/config did not return `gacha`' }
    if ($null -eq $gcfg.gacha.pools) { Fail 'admin/config gacha has no `pools` map' }
    $gcfg.gacha.pools.standard.five = @('weap-svadilfari')
    $saved = Invoke-RestMethod -Uri "$base/api/game/admin/config" -Method Post -Headers $hdrs -ContentType 'application/json' -Body (@{ gacha = $gcfg.gacha } | ConvertTo-Json -Depth 12)
    if ($saved.gacha.pools.standard.five -notcontains 'weap-svadilfari') { Fail 'admin/config did not persist the pool list' }
    Write-Host '  CAUGHT forwarded admin/config -> gacha pools round-tripped'

    $badPool = $gcfg.gacha
    $badPool.pools.standard.five = @('weap-does-not-exist')
    $rejected = 0
    try {
        $null = Invoke-RestMethod -Uri "$base/api/game/admin/config" -Method Post -Headers $hdrs -ContentType 'application/json' -Body (@{ gacha = $badPool } | ConvertTo-Json -Depth 12)
    } catch { $rejected = $_.Exception.Response.StatusCode.value__ }
    if ($rejected -ne 400) { Fail ("a pool list with an unknown id was accepted (status " + $rejected + ", expected 400)") }
    Write-Host '  CAUGHT an unregistered pool id is rejected with 400'

    $reset = Invoke-RestMethod -Uri "$base/api/game/admin/reset-daily" -Method Post -Headers $hdrs -ContentType 'application/json' -Body '{}'
    if (-not $reset.ok) { Fail 'admin/reset-daily did not report ok' }
    Write-Host ("  CAUGHT forwarded admin/reset-daily -> " + $reset.count + " player(s)")

    # The auth boundary itself: the SECOND registrant is not an admin.
    $user2 = $user + '_b'
    $null = Invoke-RestMethod -Uri "$base/api/auth/register" -Method Post -ContentType 'application/json' -Body (@{ username = $user2; password = $pass; nickname = $user2 } | ConvertTo-Json)
    $auth2 = Invoke-RestMethod -Uri "$base/api/game/auth" -Method Post -ContentType 'application/json' -Body (@{ username = $user2; password = $pass } | ConvertTo-Json)
    $nonAdmin = 0
    try {
        $null = Invoke-RestMethod -Uri "$base/api/game/admin/players" -Method Get -Headers @{ Authorization = "Bearer $($auth2.token)" }
    } catch { $nonAdmin = $_.Exception.Response.StatusCode.value__ }
    if ($nonAdmin -ne 403) { Fail ("a non-admin reached admin/players (status " + $nonAdmin + ", expected 403)") }
    Write-Host '  CAUGHT a non-admin is refused with 403'

    Write-Host ''
    Write-Host 'Forwarding smoke: PASS - the website forwards /api/game/* to the .NET game service,'
    Write-Host 'derives the identity from the server-side session, keeps auth on the Node side,'
    Write-Host 'and serves the admin surface under the contract public/admin-game.html expects.'
    Cleanup
    exit 0
}
catch {
    Cleanup
    throw
}