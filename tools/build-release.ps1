# ============================================================
#  WaningOath - build the playable release package
#
#  Output: dist\WaningOath\
#    client\   Godot 导出的客户端（自包含 .NET）
#    server\   自包含的 .NET 服务端
#    data\     共享静态数据 + 存档
#    StartGame.cmd / StopServer.cmd / 说明.txt
#
#  WHY THE NUGET DANCE BELOW:
#    Godot 的 C# 导出会以 self-contained + win-x64 发布，需要 nuget.org 上的
#    运行时包；而仓库的 nuget.config 为了离线构建把源清得只剩 Godot 自带的
#    nupkgs。所以导出期间临时加一个官方源，导出完立刻还原（见 finally）。
#    第一次导出后包会缓存在 .nuget\packages，之后离线也能过。
#
#  ASCII ONLY: PowerShell 5.1 reads a BOM-less .ps1 as ANSI.
# ============================================================
param(
    [string]$OutRoot = 'dist\WaningOath',
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

function Say($msg) { Write-Host "[release] $msg" }

# 1) .NET 环境 + 构建
Say 'building solution ...'
cmd /c "call tools\env.cmd >nul && dotnet build WaningOath.sln -c Release -maxcpucount:1 -v:q" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

# 2) 目录骨架
$root = Join-Path $repo $OutRoot
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $root 'client'), (Join-Path $root 'server') | Out-Null

# 3) 导出客户端（临时开 nuget.org）
$nuget = Join-Path $repo 'nuget.config'
$nugetBak = Join-Path $env:TEMP 'waningoath-nuget.config.bak'
Copy-Item $nuget $nugetBak -Force
try {
    $cfg = Get-Content $nuget -Raw
    if ($cfg -notmatch 'api\.nuget\.org') {
        $cfg = $cfg.Replace(
            '<add key="godot-local" value="Godot4\Godot_v4.7.1-stable_mono_win64\GodotSharp\Tools\nupkgs" />',
            '<add key="godot-local" value="Godot4\Godot_v4.7.1-stable_mono_win64\GodotSharp\Tools\nupkgs" />' + "`r`n" +
            '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />')
        [IO.File]::WriteAllText($nuget, $cfg, (New-Object Text.UTF8Encoding($false)))
    }

    Say 'exporting client ...'
    $godot = Join-Path $repo 'Godot4\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe'
    $clientExe = Join-Path $root 'client\WaningOath.exe'
    cmd /c "call tools\env.cmd >nul && `"$godot`" --headless --path WaningOath --export-release `"Windows Desktop`" `"$clientExe`" 2>&1" | Out-Null
    if (-not (Test-Path $clientExe)) { throw 'client export failed (no exe produced)' }
}
finally {
    Copy-Item $nugetBak $nuget -Force
    Remove-Item $nugetBak -ErrorAction SilentlyContinue
}

# 4) 服务端（自包含，目标机不需要装 .NET）
Say 'publishing server (self-contained win-x64) ...'
$serverOut = Join-Path $root 'server'
cmd /c "call tools\env.cmd >nul && dotnet publish WaningOath.Server\WaningOath.Server.csproj -c Release -r win-x64 --self-contained true -o `"$serverOut`" 2>&1" | Out-Null
if (-not (Test-Path (Join-Path $serverOut 'WaningOath.Server.exe'))) { throw 'server publish failed' }

# 5) 共享数据 + 启动脚本
Say 'copying data ...'
Copy-Item (Join-Path $repo 'data') (Join-Path $root 'data') -Recurse -Force
Get-ChildItem $root -Recurse -File -Filter *.pdb | Remove-Item -Force

foreach ($name in 'StartGame.cmd', 'StopServer.cmd', '说明.txt') {
    $src = Join-Path $repo "tools\release\$name"
    if (Test-Path $src) { Copy-Item $src (Join-Path $root $name) -Force }
}

Say ("package: {0:N0} MB -> {1}" -f ((Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $root)

if ($Zip) {
    $zip = Join-Path $repo ("dist\WaningOath-" + (Get-Date -Format 'yyyyMMdd') + '.zip')
    Remove-Item $zip -ErrorAction SilentlyContinue
    Say 'compressing ...'
    Compress-Archive -Path $root -DestinationPath $zip -CompressionLevel Optimal
    Say ("zip: {0:N0} MB -> {1}" -f ((Get-Item $zip).Length / 1MB), $zip)
}
