@echo off
setlocal
cd /d "%~dp0.."

echo [1/6] Release build
call tools\build.cmd
if errorlevel 1 exit /b 1

echo.
echo [2/6] Data validation
call tools\run-sim.cmd validate
if errorlevel 1 exit /b 1

echo.
echo [3/6] Manual battle determinism
call tools\run-sim.cmd manual
if errorlevel 1 exit /b 1

echo.
echo [4/6] Loadout battle determinism
call tools\run-sim.cmd manual --loadout graduate
if errorlevel 1 exit /b 1

echo.
echo [5/6] Live API smoke
python tools\api-smoke.py
if errorlevel 1 exit /b 1

echo.
echo [6/6] Godot UI smoke (22 screens)
powershell.exe -ExecutionPolicy Bypass -File tools\godot-smoke.ps1
if errorlevel 1 exit /b 1

echo.
echo All acceptance checks passed.
endlocal
