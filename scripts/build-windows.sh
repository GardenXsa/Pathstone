#!/usr/bin/env bash
# WIN-INSTALLER (issue #4): build a self-contained single-file
# Windows x64 binary of Pathstone. Output goes to
# `publish/win-x64/` (containing `MyGame.Desktop.exe` + the native
# Avalonia libraries). The NSIS installer script
# (`installer/pathstone.nsi`) packages this folder into a Windows
# installer .exe.
#
# Usage (from the repository root, i.e. the `desktop-app/` dir):
#     ./scripts/build-windows.sh
#
# Prerequisites:
#   * .NET 8 SDK (`dotnet --version` ≥ 8.0).
#   * No Windows-only toolchain required — `dotnet publish` can
#     cross-publish to win-x64 from Linux/macOS.
#
# The build is unsigned (see closed #56 — the installer will trigger
# a SmartScreen warning on first run; users click "More info" →
# "Run anyway").

set -euo pipefail

# Resolve the repo root (the `desktop-app/` directory) relative to
# this script. The script lives in `desktop-app/scripts/`.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

PROJECT="${REPO_ROOT}/src/MyGame.Desktop/MyGame.Desktop.csproj"
OUT_DIR="${REPO_ROOT}/publish/win-x64"

echo "==> Publishing Pathstone for win-x64 (self-contained, single-file)…"
echo "    Project: ${PROJECT}"
echo "    Output:  ${OUT_DIR}"
echo

dotnet publish "${PROJECT}" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "${OUT_DIR}"

echo
echo "==> Publish complete."
echo "    Entry point: ${OUT_DIR}/MyGame.Desktop.exe"
echo
echo "Next step: build the installer with NSIS:"
echo "    makensis ${REPO_ROOT}/installer/pathstone.nsi"
echo "    (produces ${REPO_ROOT}/installer/Pathstone-Setup-0.2.0.exe)"
