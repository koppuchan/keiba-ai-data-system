@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the Content Generator batch (spec section 11).
REM Run this a few minutes after each scheduled score run, so today's AI index
REM is available before picks are generated.
REM
REM Differences from run-content.bat, which is for interactive use:
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
set LOGFILE=logs\content-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] content batch start >> "%LOGFILE%"

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
"%EXE%" content %1 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] content batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
