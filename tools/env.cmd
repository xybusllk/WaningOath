@echo off
REM ============================================================
REM  Waning Oath - development environment entry
REM  Usage: open cmd or PowerShell and run  tools\env.cmd  first.
REM
REM  This machine has NO global .NET SDK (only runtimes), and the
REM  bundled Godot is the non-C# build. So everything the project
REM  needs is installed inside this workspace:
REM     .dotnet\      .NET SDK 9.0.318
REM     Godot4\       Godot 4.7.1 (.NET build, C# capable)
REM
REM  APPDATA / NuGet are redirected into the workspace so that the
REM  toolchain is fully self-contained and also works inside
REM  restricted (sandboxed) shells. Side effect: the Godot editor
REM  settings launched from this shell live in .appdata\Godot.
REM
REM  Keep this file ASCII-only: cmd.exe reads it in the OEM
REM  codepage, and non-ASCII bytes break line parsing.
REM ============================================================
for %%i in ("%~dp0..") do set "WANING_ROOT=%%~fi"

set "DOTNET_ROOT=%WANING_ROOT%\.dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_HOME=%WANING_ROOT%\.dotnet-home"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

set "APPDATA=%WANING_ROOT%\.appdata"
set "NUGET_PACKAGES=%WANING_ROOT%\.nuget\packages"
set "NUGET_HTTP_CACHE_PATH=%WANING_ROOT%\.nuget\http-cache"

set "GODOT=%WANING_ROOT%\Godot4\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe"

if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"
if not exist "%APPDATA%" mkdir "%APPDATA%"
if not exist "%NUGET_PACKAGES%" mkdir "%NUGET_PACKAGES%"
if not exist "%NUGET_HTTP_CACHE_PATH%" mkdir "%NUGET_HTTP_CACHE_PATH%"

echo [WaningOath] WANING_ROOT = %WANING_ROOT%
echo [WaningOath] DOTNET_ROOT = %DOTNET_ROOT%
echo [WaningOath] GODOT       = %GODOT%
echo [WaningOath] dotnet version:
dotnet --version
