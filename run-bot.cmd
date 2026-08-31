@echo off
REM Starts the published bot for a long-running session on this machine.
REM
REM appsettings.json is tuned for GitHub Actions, where the 10-minute cron is the scan
REM cadence and a run is a single pass (Run:MaxRunMinutes = 0). A machine that stays on has
REM no cron to lean on, so the scan window is widened here rather than in the file:
REM publish.cmd overwrites appsettings.json, and an environment variable survives that.
REM
REM Note the bot is deployed to GitHub Actions; this script is for running it on a machine
REM instead, and is not needed alongside the workflow.
REM
REM The process exits after this many minutes and the scheduled task's repeating trigger
REM starts it again, which doubles as a watchdog if it ever dies early.
setlocal
set Run__MaxRunMinutes=1435
"%~dp0publish\CoslyHighPriceBot.exe"
endlocal
