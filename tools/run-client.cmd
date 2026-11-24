@echo off
rem ============================================================
rem  WaningOath - launch the Godot client (for manual acceptance)
rem
rem  WHY THIS EXISTS
rem  Running `godot --path WaningOath` from a command line needs a
rem  PREBUILT assembly, and it has to be the Debug one:
rem  .godot\mono\temp\bin\Debug\WaningOath.dll
rem  tools\build.cmd only produces Release (Sim / Server do not care),
rem  so a plain CLI launch fails with
rem    "Cannot instantiate C# script because the associated class could
rem     not be found. Script: 'res://Scripts/GameRoot.cs'"
rem  which reads like a code bug and is not one. This script builds the
rem  Debug assembly first, then starts the client.
rem
rem  Opening the project in the Godot editor (F5) works without this --
rem  the editor builds Debug by itself. This is for the no-editor path.
rem
rem  ASCII ONLY: cmd.exe reads this file in the OEM code page, so a CJK
rem  byte in here breaks line parsing (same rule as build.cmd).
rem ============================================================

setlocal
call "%~dp0env.cmd" >nul 2>&1

echo [client] building the Debug assembly Godot expects ...
dotnet build "%~dp0..\WaningOath\WaningOath.csproj" -c Debug -maxcpucount:1 -v:q
if errorlevel 1 (
  echo [client] build failed -- see the output above.
  exit /b 1
)

set GODOT=%~dp0..\Godot4\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe
if not exist "%GODOT%" (
  echo [client] Godot not found at %GODOT%
  exit /b 1
)

rem Never inherit the headless layout-smoke switches from a test shell:
rem WO_UI_SMOKE=1 builds every screen, WO_UI_SIZE rewrites the stretch
rem baseline. Both belong to tools\godot-smoke.ps1, not a normal launch.
set "WO_UI_SMOKE="
set "WO_UI_SIZE="

echo [client] starting the client (two first-class resolutions: 1920x1080 and 1440x1080) ...
if not exist "%~dp0..\sim-out" mkdir "%~dp0..\sim-out"
start "" /D "%~dp0..\WaningOath" "%GODOT%" --path "%~dp0..\WaningOath" --log-file "%~dp0..\sim-out\client.log"
rem Give Godot time to create its process. `start` returns 0 even when the app
rem is blocked by Windows policy, so a silent "nothing happened" is the normal
rem symptom. Check the process instead of trusting `start`.
ping -n 6 127.0.0.1 >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Get-Process | Where-Object { $_.ProcessName -like 'Godot_v4.7.1*' } | Select-Object -First 1) { exit 0 } else { exit 1 }"
if errorlevel 1 (
  echo.
  echo [client] Godot did not stay open.
  echo [client] log: %~dp0..\sim-out\client.log
  echo [client] If the log is empty or Windows showed a security prompt,
  echo [client] the executable may have been blocked by SmartScreen / WDAC /
  echo [client] AppLocker / Defender. Run tools\diagnose-launch.ps1 and see
  echo [client] the Windows security notes in the project docs (06).
  exit /b 1
)
echo [client] started. log: %~dp0..\sim-out\client.log
endlocal
