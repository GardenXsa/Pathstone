#!/usr/bin/env bash
# Package Pathstone as an AppImage for Linux (issue #53).
# Requires: scripts/build-linux.sh has been run (publish/linux-x64/ exists).
# Requires: appimagetool (https://github.com/AppImage/AppImageKit).
# Produces: publish/Pathstone-x86_64.AppImage
set -euo pipefail
cd "$(dirname "$0")/.."

APPDIR="publish/Pathstone.AppDir"

if [ ! -d "publish/linux-x64" ]; then
  echo "Error: publish/linux-x64/ not found. Run scripts/build-linux.sh first."
  exit 1
fi

echo "Creating AppDir structure..."
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# Copy binary.
cp -r publish/linux-x64/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/MyGame.Desktop"

# .desktop file.
cat > "$APPDIR/pathstone.desktop" << 'DESKTOP'
[Desktop Entry]
Name=Pathstone
Comment=Desktop multiplayer narrative RPG with AI Game Master
Exec=MyGame.Desktop
Icon=pathstone
Terminal=false
Type=Application
Categories=Game;RolePlaying;
DESKTOP

# AppRun script.
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/usr/bin/env bash
SELF="$(readlink -f "$0")"
HERE="$(dirname "$SELF")"
exec "$HERE/usr/bin/MyGame.Desktop" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# Placeholder icon (a 1x1 PNG if no real icon exists — replace with real art).
if [ -f "public/logo.svg" ]; then
  # Try to convert SVG to PNG if rsvg-convert is available.
  if command -v rsvg-convert &>/dev/null; then
    rsvg-convert -w 256 -h 256 public/logo.svg -o "$APPDIR/pathstone.png"
  else
    # Fallback: create a simple 256x256 dark blue PNG.
    python3 -c "
import struct, zlib
def png(w,h,data):
  def chunk(t,d): return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
  sig=b'\x89PNG\r\n\x1a\n'
  ihdr=struct.pack('>IIBBBBB',w,h,8,2,0,0,0)
  raw=b''
  for y in range(h): raw+=b'\x00'+data
  idat=zlib.compress(raw)
  return sig+chunk(b'IHDR',ihdr)+chunk(b'IDAT',idat)+chunk(b'IEND',b'')
open('$APPDIR/pathstone.png','wb').write(png(256,256,b'\x0f\x11\x15'*256))
" 2>/dev/null || true
  fi
fi
cp "$APPDIR/pathstone.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/pathstone.png" 2>/dev/null || true

# Build AppImage.
if command -v appimagetool &>/dev/null; then
  appimagetool "$APPDIR" "publish/Pathstone-x86_64.AppImage"
  echo "Done: publish/Pathstone-x86_64.AppImage"
else
  echo "appimagetool not found. AppDir created at: $APPDIR"
  echo "Install appimagetool from https://github.com/AppImage/AppImageKit"
  echo "Then run: appimagetool $APPDIR publish/Pathstone-x86_64.AppImage"
fi
