#!/bin/bash
# v2rayN macOS ARM64 Rebuild and Launch Script
set -e

cd "$(dirname "$0")/.."
PROJECT_ROOT="$(pwd)"
cd "v2rayN"  # Enter v2rayN subdirectory
WORKING_DIR="$(pwd)"

APP_NAME="v2rayN"
BUILD_CONFIG="Release"
TARGET_RUNTIME="osx-arm64"
BUILD_OUTPUT="v2rayN.Desktop/bin/$BUILD_CONFIG/net10.0/$TARGET_RUNTIME/publish"
APP_BUNDLE="$BUILD_OUTPUT/$APP_NAME.app"

echo "==> [1/5] Building $APP_NAME for $TARGET_RUNTIME..."
DOTNET_CLI="${DOTNET_CLI:-$HOME/.dotnet/dotnet}"
if [ ! -x "$DOTNET_CLI" ]; then
    DOTNET_CLI="$(command -v dotnet)"
fi

# Build and publish
"$DOTNET_CLI" publish v2rayN.Desktop/v2rayN.Desktop.csproj \
    -c $BUILD_CONFIG \
    -r $TARGET_RUNTIME \
    --self-contained true

echo "==> Build succeeded."

echo "==> [2/5] Creating macOS app bundle..."
cd "$BUILD_OUTPUT"

# Create app bundle structure
rm -rf "$APP_NAME.app"
mkdir -p "$APP_NAME.app/Contents/"{MacOS,Resources}

# Copy ALL files from publish directory
cp -f v2rayN "$APP_NAME.app/Contents/MacOS/"
cp -rf *.dylib "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true
cp -rf *.dll "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true
cp -rf *.json "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true
cp -rf *.pdb "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true

chmod +x "$APP_NAME.app/Contents/MacOS/v2rayN"

# Copy icon
cp -f v2rayN.icns "$APP_NAME.app/Contents/Resources/" 2>/dev/null || true
cp -f v2rayN.png "$APP_NAME.app/Contents/Resources/" 2>/dev/null || true

# Create Info.plist
cat > "$APP_NAME.app/Contents/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>v2rayN</string>
    <key>CFBundleIconFile</key>
    <string>v2rayN.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.v2rayn.desktop</string>
    <key>CFBundleName</key>
    <string>v2rayN</string>
    <key>CFBundleVersion</key>
    <string>7.22.7</string>
    <key>CFBundleShortVersionString</key>
    <string>7.22.7</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

echo "==> App bundle created."

echo "==> [3/5] Killing running $APP_NAME and cores..."
# Kill v2rayN GUI
if pgrep -x "$APP_NAME" > /dev/null 2>&1; then
    pkill -x "$APP_NAME"
    # Wait for process to exit (up to 5 seconds)
    for i in $(seq 1 50); do
        if ! pgrep -x "$APP_NAME" > /dev/null 2>&1; then
            echo "==> $APP_NAME terminated."
            break
        fi
        sleep 0.1
    done
    # Force kill if still running
    if pgrep -x "$APP_NAME" > /dev/null 2>&1; then
        echo "==> Force killing $APP_NAME..."
        pkill -9 -x "$APP_NAME"
        sleep 0.5
    fi
else
    echo "==> $APP_NAME is not running, skipping kill."
fi

# Kill proxy cores (sing-box, xray)
for CORE in sing-box xray mihomo; do
    if pgrep -x "$CORE" > /dev/null 2>&1; then
        echo "==> Killing $CORE..."
        pkill -x "$CORE" 2>/dev/null || true
        sleep 0.3
    fi
done

echo "==> [4/5] Installing to /Applications..."
INSTALLED_APP="/Applications/$APP_NAME.app"
rm -rf "$INSTALLED_APP"
cp -R "$WORKING_DIR/$APP_BUNDLE" "$INSTALLED_APP"
echo "==> Installed. Launching $INSTALLED_APP..."
open "$INSTALLED_APP"

echo "==> [5/5] Waiting for $APP_NAME to start..."
for i in $(seq 1 50); do
    if pgrep -x "$APP_NAME" > /dev/null 2>&1; then
        echo "==> $APP_NAME is running (PID: $(pgrep -x "$APP_NAME"))."
        echo ""
        echo "Success. New menu item available:"
        echo "   -> Exit and keep core running"
        exit 0
    fi
    sleep 0.1
done

echo "==> $APP_NAME did not start within 5 seconds."
echo "Check logs with: tail -f ~/Library/Logs/v2rayN/*.log"
exit 1
