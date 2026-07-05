#!/usr/bin/env bash
# Build Pathstone for Linux (issue #53).
# Produces a self-contained single-file binary at publish/linux-x64/.
set -euo pipefail
cd "$(dirname "$0")/.."
echo "Building Pathstone for Linux (linux-x64)..."
dotnet publish src/MyGame.Desktop/MyGame.Desktop.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish/linux-x64
chmod +x publish/linux-x64/MyGame.Desktop
echo "Done. Output: publish/linux-x64/MyGame.Desktop"
