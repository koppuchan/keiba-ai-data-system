@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the stalled-update check
REM (client request: notify when updates stop for a while). Runs every 15 minutes in the daytime.
REM
REM Differences from running the exe by hand:
REM   - No "pause" at the end.
REM   - Appends stdout/stderr to a dated log file under logs\.
REM   - Propagates the exit code so Task Scheduler's Last Run Result is
REM     meaningful (0 = success).
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if not exist logs mkdir logs

for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\healthcheck-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] healthcheck batch start >> "%LOGFILE%"

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
"%EXE%" healthcheck >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] healthcheck batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
