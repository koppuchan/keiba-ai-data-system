@echo off
chcp 65001 >nul
REM Prints the monitoring dashboard (spec section 17) and pushes it to
REM WordPress (Settings > Keiba AI Digest) so it can be checked without
REM SSH access to the VPS.
REM
REM %1 is an optional yyyy-MM-dd date to check a past day instead of today.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe dashboard %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
