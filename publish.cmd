@echo off
REM Builds publish\CoslyHighPriceBot.exe ready for Task Scheduler.
REM Note: overwrites publish\appsettings.json with the project's copy.
setlocal
cd /d "%~dp0"
dotnet publish src\CoslyHighPriceBot\CoslyHighPriceBot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
endlocal
