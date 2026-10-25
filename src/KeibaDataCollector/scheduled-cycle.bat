@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the periodic pipeline of ONE data source:
REM   score (AI index) -> trend (morning/live/final) -> content (WordPress)
REM in a single process, so the steps never overlap and the RACE data is read once.
REM
REM %1 is required: jv (JV-Link, central) or uma (UmaConn, local).
REM Register two tasks with the same script (see register-scheduled-tasks.ps1).
REM
REM Differences from run-*.bat, which are for interactive use:
REM   - No "pause" at the end.
REM   - Appends stdout/stderr to a dated, source-named log file under logs\.
REM   - Propagates the exit code so Task Scheduler's Last Run Result is
REM     meaningful (0 = success).
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if "%1"=="" (
    echo [ERROR] source argument required: jv or uma.
    exit /b 1
)

if not exist logs mkdir logs

for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\cycle-%1-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] cycle %1 start >> "%LOGFILE%"

if not exist "%~dp0secrets.local.bat" (
    echo [ERROR] secrets.local.bat not found. >> "%LOGFILE%"
    exit /b 1
)
call "%~dp0secrets.local.bat" >> "%LOGFILE%" 2>&1

if not defined JvLinkSoftwareId (
    echo [ERROR] JvLinkSoftwareId is not set. secrets.local.bat did not apply. >> "%LOGFILE%"
    exit /b 1
)

set EXE=%~dp0bin\Debug\net48\KeibaDataCollector.exe
if not exist "%EXE%" (
    echo [ERROR] Not built yet: %EXE% >> "%LOGFILE%"
    exit /b 1
)

pushd "%~dp0bin\Debug\net48"
"%EXE%" cycle %1 %2 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] cycle %1 end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
