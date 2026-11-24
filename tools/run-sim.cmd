@echo off
REM ============================================================
REM  WaningOath - run the simulator (WaningOath.Sim)
REM
REM  Usage:  tools\run-sim.cmd [subcommand]
REM            (none)          self-check: toolchain + deterministic RNG
REM            battle          single battle log                 [M1, done]
REM            verify          02 sec.9 pipeline + replay check [M1, done]
REM            validate        data validator, 0 errors to pass [M3, done]
REM            batch --runs N  four-tier balance simulation     [M2, done]
REM
REM  Common options: --data <dir> points the run at another data/ tree
REM  (used by tools\validate-faults.ps1 to prove the validator really catches faults).
REM
REM  Keep this file ASCII-only (see tools\build.cmd for the reason).
REM ============================================================

setlocal
call "%~dp0env.cmd" >nul
cd /d "%~dp0.."

dotnet run --project WaningOath.Sim -c Release -- %*

set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
