@echo off
chcp 65001 >nul
REM Cross-checks predictions (Nerai/Ana/Kiken/AI-index-TOP5) against confirmed
REM finishing positions and records the result to the verification table
REM (spec section 18). Safe to run repeatedly during/after the racing day -
REM already-verified predictions are skipped, and not-yet-finished races are
REM simply left for the next run.
REM
REM %1 is an optional yyyy-MM-dd date to re-verify a past day instead of today.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe verify %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
