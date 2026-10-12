@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Собирает один файл PhotoCommander.exe, который работает без установленного .NET
dotnet publish src\PhotoCommander -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\win-x64
del /q publish\win-x64\*.pdb 2>nul
echo.
echo Готово: %~dp0publish\win-x64\PhotoCommander.exe
pause
