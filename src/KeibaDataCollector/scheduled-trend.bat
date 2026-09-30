@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the Trend Engine batch (spec section 10).
REM
REM %1 is required: morning | live | final. Register three separate scheduled
REM tasks pointing at this same script with different %1 values:
REM   - morning: once, early (e.g. 07:30, right after KeibaAiDataSystem-Morning)
REM   - live:    every 30-60 min through the racing day
REM   - final:   once, after the last race of the day is expected to finish
REM
REM Differences from run-trend.bat, which is for interactive use:
REM   - No "pause" at the end.
REM   - Appends stdout/stderr to a dated, stage-named log file under logs\.
REM   - Propagates the exit code so Task Scheduler's Last Run Result is
REM     meaningful (0 = success).
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if "%1"=="" (
    echo [ERROR] stage argument required: morning, live, or final.
    exit /b 1
)

if not exist logs mkdir logs

for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\trend-%1-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] trend %1 batch start >> "%LOGFILE%"

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
"%EXE%" trend %1 %2 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] trend %1 batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
