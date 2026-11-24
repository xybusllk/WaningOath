@echo off
REM ============================================================
REM  WaningOath - build the whole solution (all four projects)
REM
REM  Usage:  tools\build.cmd [extra dotnet build args]
REM
REM  Why -maxcpucount:1 is pinned HERE instead of in the .sln:
REM    This workspace carries a trimmed .NET SDK under .dotnet\ (there is
REM    no global SDK on this machine). On that SDK, a SOLUTION-LEVEL build
REM    with parallel MSBuild nodes fails silently:
REM        "build failed   0 warnings   0 errors"
REM    with no diagnostics at all, while every single project builds fine
REM    on its own. Switching to .slnx or dropping the Godot project does
REM    NOT avoid it. Single-node builds are green.
REM    A Directory.Build.rsp is NOT read by "dotnet build", so the pin has
REM    to live on the command line, i.e. in this wrapper.
REM    Recorded in the tech-architecture doc (06), section 8.4.
REM
REM  Keep this file ASCII-only: cmd.exe reads it in the OEM codepage and
REM  non-ASCII bytes break line parsing (same rule as env.cmd).
REM
REM  NOTE: this file used to carry a CJK filename in the line above, and that
REM  single line made every build print a dozen bogus "'ild' is not recognized
REM  as an internal or external command" lines on stderr -- exactly the failure
REM  the ASCII-only rule warns about. Harmless-looking, but it is stderr noise
REM  on every single build, i.e. the place a real error would hide.
REM ============================================================

setlocal
call "%~dp0env.cmd" >nul
cd /d "%~dp0.."

dotnet build WaningOath.sln -c Release -maxcpucount:1 %*

set "EXITCODE=%ERRORLEVEL%"
endlocal & exit /b %EXITCODE%
