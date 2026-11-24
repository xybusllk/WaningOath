@echo off
rem ============================================================
rem  WaningOath - START EVERYTHING (game service + website)
rem
rem  THIS IS THE ENTRY POINT. One command, two server processes:
rem
rem    1. WaningOath.Server on 127.0.0.1:8720   (the game backend)
rem    2. the website      on 127.0.0.1:8080   (accounts + /api/game/* forward)
rem
rem  Starting ONLY the website is not enough -- the game logic lives in
rem  the .NET process, and the website merely forwards to it. That is the
rem  root cause of the "the client cannot connect" symptom.
rem
rem  The client probes BOTH addresses, so it also works when only the
rem  backend is running (direct mode, no accounts).
rem
rem  Two windows are opened on purpose: each keeps printing its own log,
rem  and closing one stops only that half.
rem
rem  ASCII ONLY: cmd.exe reads this file in the OEM code page.
rem ============================================================

setlocal
echo [all] starting the .NET game service on :8720 ...
start "WaningOath server :8720" cmd /k call "%~dp0run-server.cmd" 8720

echo [all] starting the website on :8080 (forwarding to :8720) ...
start "WaningOath website :8080" cmd /k call "%~dp0run-website.cmd"

echo.
echo [all] both are starting. Give them ~5 seconds, then:
echo         tools\run-client.cmd      to play
echo         http://127.0.0.1:8080     the website (accounts / admin page)
echo.
echo [all] if the client says "not connected", check that BOTH windows
echo       stayed open -- a build error or a busy port kills one of them.
endlocal
