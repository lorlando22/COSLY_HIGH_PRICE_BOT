@echo off
REM Genera publish\CoslyHighPriceBot.exe listo para el Programador de tareas.
REM Ojo: sobrescribe publish\appsettings.json con el del proyecto.
setlocal
cd /d "%~dp0"
dotnet publish src\CoslyHighPriceBot\CoslyHighPriceBot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
endlocal
