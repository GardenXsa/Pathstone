#!/usr/bin/env bash
# Package Pathstone into a macOS .app bundle (issue #52).
# Requires: scripts/build-macos.sh has been run (publish/osx-x64/ exists).
# Produces: publish/Pathstone.app/
set -euo pipefail
cd "$(dirname "$0")/.."

APP_DIR="publish/Pathstone.app"
CONTENTS="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS/MacOS"
RESOURCES_DIR="$CONTENTS/Resources"

if [ ! -d "publish/osx-x64" ]; then
  echo "Error: publish/osx-x64/ not found. Run scripts/build-macos.sh first."
  exit 1
}

echo "Creating .app bundle structure..."
rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Copy the self-contained binary + native libs.
cp -r publish/osx-x64/* "$MACOS_DIR/"

# Info.plist — macOS app metadata.
cat > "$CONTENTS/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Pathstone</string>
  <key>CFBundleDisplayName</key>
  <string>Pathstone</string>
  <key>CFBundleIdentifier</key>
  <string>com.gardenxsa.pathstone</string>
  <key>CFBundleVersion</key>
  <string>0.3.0</string>
  <key>CFBundleShortVersionString</key>
  <string>0.3.0</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>MyGame.Desktop</string>
  <key>LSMinimumSystemVersion</key>
  <string>10.15</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

# Make the executable actually executable.
chmod +x "$MACOS_DIR/MyGame.Desktop"

echo "Done: $APP_DIR"
echo "Note: This bundle is unsigned. macOS Gatekeeper will warn on first launch."
echo "      Users can right-click → Open to bypass, or run: xattr -cr $APP_DIR"
