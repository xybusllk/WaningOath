@echo off
rem ============================================================
rem  WaningOath - launcher
rem
rem  Starts the game service (server/WaningOath.Server.exe on :8720),
rem  waits for it to listen, then starts the client.
rem
rem  ASCII ONLY: cmd.exe reads this file in the OEM code page.
rem ============================================================
setlocal
cd /d "%~dp0"

echo [WaningOath] starting game service on :8720 ...
start "WaningOath Service" /min "%~dp0server\WaningOath.Server.exe" --port 8720 --store "%~dp0data\game\players" --audit "%~dp0data\game\audit.jsonl"

timeout /t 2 /nobreak >nul

echo [WaningOath] starting client ...
start "" "%~dp0client\WaningOath.exe"

endlocal
