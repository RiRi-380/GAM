#!/bin/bash
# Shell script to copy resources to output directories

CONFIGURATION=${1:-Debug}
SOURCE_DIR="$(dirname "$0")"
TARGET_DIR="$SOURCE_DIR/bin/$CONFIGURATION/net6.0"

# Create Resources directory if it doesn't exist
RESOURCES_TARGET="$TARGET_DIR/Resources"
mkdir -p "$RESOURCES_TARGET"

# Copy localization files
RESOURCES_SOURCE="$SOURCE_DIR/Resources"
if [ -d "$RESOURCES_SOURCE" ]; then
    cp "$RESOURCES_SOURCE"/*.json "$RESOURCES_TARGET/" 2>/dev/null
    echo "Copied localization files to $RESOURCES_TARGET"
fi

echo "Resource copy complete for $CONFIGURATION configuration"