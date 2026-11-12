#!/bin/sh
# Turns a published macOS build of Elio into Elio.app and zips it. Run it on a Mac:
#
#   sh build/package-macos.sh <version> <rid> <publish-dir> <out-dir>
#
# <rid> is osx-arm64 or osx-x64, and <publish-dir> is what
# "dotnet publish src/Elio.Desktop -c Release -r <rid>" leaves: the program and the libraries beside it.
#
# Signing: with MACOS_SIGN_IDENTITY set to a "Developer ID Application: ..." certificate in the keychain the
# app is signed with it and the hardened runtime, ready to be notarized. Without it the app gets an ad-hoc
# signature, which runs on the Mac it was built on and, elsewhere, only after the user allows it in
# System Settings > Privacy & Security.
set -eu

VERSION=$1
RID=$2
PUBLISH=$3
OUT=$4

ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
APP=$WORK/Elio.app

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH/elio" "$PUBLISH"/*.dylib "$APP/Contents/MacOS/"
chmod 755 "$APP/Contents/MacOS/elio"

# The icon, drawn from the same picture the website uses, in every size macOS asks for.
ICONSET=$WORK/Elio.iconset
mkdir "$ICONSET"
qlmanage -t -s 1024 -o "$WORK" "$ROOT/web/Elio.Web/wwwroot/favicon.svg" > /dev/null 2>&1
if [ -f "$WORK/favicon.svg.png" ]; then
    for size in 16 32 128 256 512; do
        sips -z $size $size "$WORK/favicon.svg.png" --out "$ICONSET/icon_${size}x${size}.png" > /dev/null
        double=$((size * 2))
        sips -z $double $double "$WORK/favicon.svg.png" --out "$ICONSET/icon_${size}x${size}@2x.png" > /dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Elio.icns"
fi

# CFBundleVersion takes digits and dots only, so a pre-release's "-beta.1" is left to the short version string.
BUILD_VERSION=$(echo "$VERSION" | sed 's/[-+].*//')

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Elio</string>
  <key>CFBundleDisplayName</key>
  <string>Elio</string>
  <key>CFBundleIdentifier</key>
  <string>sh.elio.app</string>
  <key>CFBundleExecutable</key>
  <string>elio</string>
  <key>CFBundleIconFile</key>
  <string>Elio</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundleVersion</key>
  <string>$BUILD_VERSION</string>
  <key>LSMinimumSystemVersion</key>
  <string>11.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>Elio connects to the servers and devices you add, which may be on your own network.</string>
</dict>
</plist>
EOF

# What the .NET runtime needs under the hardened runtime: it makes code at run time, and loads the libraries
# beside it, which are signed here but not by Apple.
cat > "$WORK/entitlements.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.cs.allow-jit</key>
  <true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
  <true/>
  <key>com.apple.security.cs.disable-library-validation</key>
  <true/>
</dict>
</plist>
EOF

IDENTITY=${MACOS_SIGN_IDENTITY:--}
if [ "$IDENTITY" = "-" ]; then
    for file in "$APP"/Contents/MacOS/*.dylib; do codesign --force --sign - "$file"; done
    codesign --force --sign - --entitlements "$WORK/entitlements.plist" "$APP"
else
    for file in "$APP"/Contents/MacOS/*.dylib; do codesign --force --timestamp --options runtime --sign "$IDENTITY" "$file"; done
    codesign --force --timestamp --options runtime --sign "$IDENTITY" --entitlements "$WORK/entitlements.plist" "$APP"
fi
codesign --verify --deep --strict "$APP"

# ditto keeps what a plain zip loses: the signature's extended attributes and the links inside a bundle.
mkdir -p "$OUT"
ditto -c -k --keepParent "$APP" "$OUT/elio-$VERSION-$RID.zip"
ls -l "$OUT/elio-$VERSION-$RID.zip"
