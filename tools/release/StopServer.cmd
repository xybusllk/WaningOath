@echo off
rem Stop the game service. The client can be closed with its own window.
setlocal
taskkill /FI "WINDOWTITLE eq WaningOath Service*" /T /F >nul 2>&1
taskkill /IM WaningOath.Server.exe /F >nul 2>&1
echo [WaningOath] service stopped.
endlocal
