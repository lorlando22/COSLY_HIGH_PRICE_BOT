@echo off
REM Starts the published bot for a long-running session on this machine.
REM
REM appsettings.json is tuned for GitHub Actions, where a run has to finish before the next
REM cron fires (Run:MaxRunMinutes = 13). A machine that stays on has no such limit, so the
REM scan window is widened here rather than in the file: publish.cmd overwrites
REM appsettings.json, and an environment variable survives that.
REM
REM The process exits after this many minutes and the scheduled task's repeating trigger
REM starts it again, which doubles as a watchdog if it ever dies early.
setlocal
set Run__MaxRunMinutes=1435
"%~dp0publish\CoslyHighPriceBot.exe"
endlocal
