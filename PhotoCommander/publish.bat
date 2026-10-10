@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Собирает один файл PhotoCommander.exe, который работает без установленного .NET
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
echo.
echo Готово: %~dp0publish\PhotoCommander.exe
pause
