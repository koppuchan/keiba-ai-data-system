@echo off
chcp 65001 >nul
REM Computes "today's trend" (pace/post-position/track/final-furlong/passage
REM tendencies, spec section 10) for whichever venues are racing today and
REM saves the result to trend_snapshots in the local SQLite.
REM
REM %1 is required: morning | live | final (see Program.cs "trend" command).
REM %2 is an optional yyyy-MM-dd date to recompute a past day instead of today.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe trend %1 %2

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
