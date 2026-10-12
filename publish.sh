#!/usr/bin/env bash
# Собирает один исполняемый файл PhotoCommander, который работает без установленного .NET.
# По умолчанию — для Linux x64; другая платформа: ./publish.sh win-x64
set -e
cd "$(dirname "$0")"
RID="${1:-linux-x64}"
dotnet publish src/PhotoCommander -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "publish/$RID"
rm -f "publish/$RID"/*.pdb
echo
echo "Готово: $(pwd)/publish/$RID/"
