@echo off
chcp 65001 >nul
REM Generates today's Nerai-uma / Ana-uma / Kiken-na-ninkiuma picks (spec
REM section 11) from the AI index already computed by run-score.bat.
REM Run run-score.bat first, or every race will have no candidates.
REM
REM %1 is an optional yyyy-MM-dd date (e.g. run-content.bat 2026-08-30) to
REM regenerate a past day instead of today.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe content %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
