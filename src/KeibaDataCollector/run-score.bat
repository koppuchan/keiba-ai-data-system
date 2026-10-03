@echo off
chcp 65001 >nul
REM Computes the 6 factors + AI index for today's runners from the backfilled
REM local SQLite (data\historical.sqlite3) and saves them to the scores table.
REM Does NOT push to WordPress (hrc_factors) - the existing horse-race-custom-builder
REM deployment already does that for the same posts; this app's content command
REM publishes the new AI index / trend / picks to a separate WordPress post type.
REM Run run-backfill.bat at least once before this, or every horse will score null.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
REM %1 is an optional yyyy-MM-dd date (e.g. run-score.bat 2026-08-30) to
REM re-score a past day instead of today.
KeibaDataCollector.exe score %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
