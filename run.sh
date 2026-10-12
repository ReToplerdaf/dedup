#!/usr/bin/env bash
# Запуск Photo Commander в Linux
set -e
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null; then
  echo "Не найден .NET SDK. Установите .NET 10 SDK: https://dot.net (в Ubuntu: sudo apt install dotnet-sdk-10.0)"
  exit 1
fi
exec dotnet run -c Release --project src/PhotoCommander
