#!/bin/bash
# v2rayN macOS Rebuild and Launch Script (Auto-detect architecture)
set -e

# Detect CPU architecture
ARCH=$(uname -m)
case "$ARCH" in
    x86_64)
        SCRIPT="rebuild-and-launch-macos-x64.sh"
        ;;
    arm64)
        SCRIPT="rebuild-and-launch-macos-arm64.sh"
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
echo "==> Detected architecture: $ARCH"
echo "==> Running: $SCRIPT"
echo ""

exec "$SCRIPT_DIR/$SCRIPT"
