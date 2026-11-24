@echo off
rem ============================================================
rem  WaningOath - start the .NET game service (the actual game backend)
rem
rem  READ THIS IF YOU COULD NOT FIND "HOW DO I START THE SERVER":
rem  There are TWO processes on the server side and BOTH are needed:
rem
rem    * WaningOath.Server  (:8720)  <- save files, stage rewards, gacha,
rem                                    growth settlement, admin API
rem    * the website        (:8080)  <- accounts, sessions, and it
rem                                    FORWARDS /api/game/* to :8720
rem
rem  Starting only the website gives you a website whose game endpoints
rem  cannot reach anything -- that is exactly the "cannot connect"
rem  symptom. Use tools\run-all.cmd unless you specifically want just
rem  the backend (e.g. while debugging with curl).
rem
rem  ASCII ONLY: cmd.exe reads this file in the OEM code page.
rem ============================================================

setlocal
call "%~dp0env.cmd" >nul 2>&1

set PORT=%~1
if "%PORT%"=="" set PORT=8720

echo [server] building WaningOath.Server (Release) ...
dotnet build "%~dp0..\WaningOath.Server\WaningOath.Server.csproj" -c Release -maxcpucount:1 -v:q
if errorlevel 1 (
  echo [server] build failed -- see the output above.
  exit /b 1
)

set DLL=%~dp0..\WaningOath.Server\bin\Release\net9.0\WaningOath.Server.dll
set EXE=%~dp0..\WaningOath.Server\bin\Release\net9.0\WaningOath.Server.exe
if not exist "%DLL%" if not exist "%EXE%" (
  echo [server] not found: %DLL% / %EXE%
  exit /b 1
)

echo [server] listening on http://127.0.0.1:%PORT%  (Ctrl+C to stop)
rem Prefer the Microsoft-signed dotnet host over the unsigned apphost exe.
rem On machines with WDAC / AppLocker / overly strict Defender policies the
rem unsigned WaningOath.Server.exe can be blocked from being launched even
rem though the DLL itself is fine. `dotnet WaningOath.Server.dll` avoids that.
if exist "%DLL%" (
  dotnet "%DLL%" --port %PORT% --store "%~dp0..\data\game\players" --audit "%~dp0..\data\game\audit.jsonl"
) else (
  "%EXE%" --port %PORT% --store "%~dp0..\data\game\players" --audit "%~dp0..\data\game\audit.jsonl"
)
endlocal
