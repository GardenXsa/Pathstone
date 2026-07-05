#!/usr/bin/env bash
# Build Pathstone for macOS (issue #52).
# Produces a self-contained single-file binary at publish/osx-x64/.
# For a .app bundle, run scripts/package-macos.sh after this.
set -euo pipefail
cd "$(dirname "$0")/.."
echo "Building Pathstone for macOS (osx-x64)..."
dotnet publish src/MyGame.Desktop/MyGame.Desktop.csproj \
  -c Release \
  -r osx-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish/osx-x64
echo "Done. Output: publish/osx-x64/MyGame.Desktop"
