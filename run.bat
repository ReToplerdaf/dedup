@echo off
chcp 65001 >nul
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Не найден .NET SDK. Установите командой:
  echo    winget install Microsoft.DotNet.SDK.10
  echo затем закройте это окно и запустите run.bat снова.
  pause
  exit /b 1
)
dotnet run -c Release --project src\PhotoCommander
if errorlevel 1 pause
