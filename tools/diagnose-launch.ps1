#requires -Version 5.1
<#
    diagnose-launch.ps1
    Read-only diagnostic for "run-all / run-client starts nothing" on Windows.
    It does not change firewall, Defender, or execution policy.
#>
$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot

function Section([string]$text) {
    Write-Host ""
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

Section "项目路径"
Write-Host "root = $root"

Section "端口 8080 / 8720"
foreach ($port in 8080, 8720) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    if ($listener) {
        Write-Host ("  {0}: LISTEN pid={1}" -f $port, ($listener | Select-Object -First 1).OwningProcess) -ForegroundColor Green
    } else {
        Write-Host "  ${port}: no listener" -ForegroundColor Yellow
    }
}

Section "进程"
$names = 'WaningOath.Server', 'dotnet', 'node', 'Godot_v4.7.1-stable_mono_win64'
$procs = Get-Process -Name $names -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Select-Object Id, ProcessName, StartTime, Path | Format-Table -AutoSize | Out-String | Write-Host
} else {
    Write-Host "  none of: $($names -join ', ')" -ForegroundColor Yellow
}

Section "签名 / MOTW"
$files = @(
    (Join-Path $root 'Godot4\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe'),
    (Join-Path $root 'WaningOath.Server\bin\Release\net9.0\WaningOath.Server.exe'),
    (Join-Path $root 'WaningOath.Server\bin\Release\net9.0\WaningOath.Server.dll'),
    (Join-Path $root '.dotnet\dotnet.exe')
)
foreach ($file in $files) {
    if (-not (Test-Path $file)) {
        Write-Host "  MISSING  $file" -ForegroundColor Yellow
        continue
    }
    $sig = Get-AuthenticodeSignature -FilePath $file
    $zone = Get-Item -Path $file -Stream Zone.Identifier -ErrorAction SilentlyContinue
    $signer = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '(none)' }
    Write-Host ("  {0}`n    status={1}`n    signer={2}`n    MOTW={3}" -f $file, $sig.Status, $signer, ($(if ($zone) { 'YES' } else { 'no' })))
}

Section "Smart App Control"
$policy = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name VerifiedAndReputablePolicyState -ErrorAction SilentlyContinue
if ($policy) {
    $state = $policy.VerifiedAndReputablePolicyState
    $text = switch ($state) {
        0 { '0 = OFF' }
        1 { '1 = ENFORCE (may block unsigned apps)' }
        2 { '2 = EVALUATION' }
        default { "$state = unknown" }
    }
    Write-Host "  VerifiedAndReputablePolicyState: $text" -ForegroundColor ($(if ($state -eq 1) { 'Red' } else { 'Green' }))
} else {
    Write-Host "  registry value missing (usually means not configured)" -ForegroundColor Yellow
}

Section "AppLocker（可能被拒绝）"
$applocker = Get-AppLockerPolicy -Effective -ErrorAction SilentlyContinue
if ($applocker) {
    $active = @($applocker.RuleCollections | Where-Object { $_.Rules.Count -gt 0 })
    if ($active.Count -eq 0) {
        Write-Host "  no active rule collections" -ForegroundColor Green
    } else {
        Write-Host "  ACTIVE AppLocker rules (may block apps outside allowed paths):" -ForegroundColor Red
        foreach ($c in $active) {
            Write-Host ("    {0}  enforcement={1}  rules={2}" -f $c.CollectionType, $c.EnforcementMode, $c.Rules.Count)
        }
        Write-Host "  inspect: Get-AppLockerPolicy -Effective -Xml"
    }
} else {
    Write-Host "  not readable (needs admin, or AppLocker not configured)" -ForegroundColor Yellow
}

Section "最近的 CodeIntegrity 事件"
Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 5 -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, Id, LevelDisplayName, @{ n = 'Message'; e = { ($_.Message -split "`r?`n")[0] } } |
    Format-Table -AutoSize | Out-String | Write-Host

Section "最近的 SmartScreen 事件"
Get-WinEvent -LogName 'Microsoft-Windows-SmartScreen/Operational' -MaxEvents 5 -ErrorAction SilentlyContinue |
    Select-Object TimeCreated, Id, LevelDisplayName, @{ n = 'Message'; e = { ($_.Message -split "`r?`n")[0] } } |
    Format-Table -AutoSize | Out-String | Write-Host
if (-not (Get-WinEvent -ListLog 'Microsoft-Windows-SmartScreen/Operational' -ErrorAction SilentlyContinue)) {
    Write-Host "  log not present on this machine" -ForegroundColor Yellow
}

Section "下一步"
Write-Host "  1) 服务器优先走签名 dotnet 宿主：tools\run-server.cmd 现在默认 dotnet WaningOath.Server.dll。"
Write-Host "  2) 客户端日志：sim-out\client.log；run-client.cmd 会在 Godot 进程没起来时报错。"
Write-Host "  3) 若 MOTW=YES：powershell -Command `"Get-ChildItem '$root' -Recurse -File | Unblock-File`""
Write-Host "  4) 若 Smart App Control=1 或 AppLocker 有拦截：需要在本机安全策略里放行，或关闭 SAC（系统级，谨慎）。"
Write-Host "  5) 若只是 8080/8720 被占：先关掉旧的 node / WaningOath.Server 进程。"
