@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for pushing the monitoring dashboard (spec
REM section 17) to WordPress. Run this every 15-30 minutes during the racing
REM day so Settings > Keiba AI Digest stays current.
REM
REM Differences from run-dashboard.bat, which is for interactive use:
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
set LOGFILE=logs\dashboard-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] dashboard batch start >> "%LOGFILE%"

if not exist "%~dp0secrets.local.bat" (
    echo [ERROR] secrets.local.bat not found. >> "%LOGFILE%"
    exit /b 1
)
call "%~dp0secrets.local.bat" >> "%LOGFILE%" 2>&1

set EXE=%~dp0bin\Debug\net48\KeibaDataCollector.exe
if not exist "%EXE%" (
    echo [ERROR] Not built yet: %EXE% >> "%LOGFILE%"
    exit /b 1
)

pushd "%~dp0bin\Debug\net48"
"%EXE%" dashboard %1 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] dashboard batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
