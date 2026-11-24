@echo off
rem ============================================================
rem  WaningOath - start the website (accounts + /api/game/* forward)
rem
rem  Normally you do NOT run this by hand -- use tools\run-all.cmd.
rem  This exists so run-all.cmd does not have to nest quotes inside a
rem  "start ... cmd /k" command line (which cmd.exe parses badly).
rem
rem  The website folder is DISCOVERED (the one containing server.js),
rem  never written out by name: it has a CJK name and this file must stay
rem  ASCII-only.
rem
rem  ASCII ONLY: cmd.exe reads this file in the OEM code page.
rem ============================================================

setlocal
set ROOT=%~dp0..

set WEB=
for /d %%d in ("%ROOT%\*") do (
  if exist "%%d\server.js" set WEB=%%d
)
if "%WEB%"=="" (
  echo [web] could not find the website folder ^(the one containing server.js^)
  exit /b 1
)

if "%GAME_PORT%"=="" set GAME_PORT=8720
if "%PORT%"=="" set PORT=8080
set WO_GAME_API=http://127.0.0.1:%GAME_PORT%
set HOST=127.0.0.1

echo [web] %WEB%
echo [web] listening on http://127.0.0.1:%PORT%  forwarding /api/game/* to %WO_GAME_API%
pushd "%WEB%"
node server.js
popd
endlocal
