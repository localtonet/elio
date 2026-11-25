#!/bin/sh
# Wraps a published Linux build of Elio in an AppImage: one file that runs on most distributions without being
# installed, with the menu entry and the icon inside it.
#
#   sh build/package-appimage.sh <version> <rid> <publish-dir> <out-dir>
#
# <rid> is linux-x64 or linux-arm64, and <publish-dir> holds the single file "elio" that
# "dotnet publish src/Elio.Desktop -c Release -r <rid>" leaves. Runs on an x86_64 or aarch64 Linux, for either
# <rid>: appimagetool for this computer packs the image, and the AppImage runtime for <rid> is put in front of it.
#
# appimagetool and the runtime are pinned below, and each download is checked against its SHA-256 before it is used.
# They are kept in native/downloads, beside the engine's. appimagetool is itself an AppImage; it is unpacked and run
# from there, so building needs no FUSE. Needs curl and sha256sum.
#
# Like the tar.gz, the image brings .NET with it but uses the libraries every desktop has: ICU, fontconfig and X11.
# A copy started from an AppImage does not replace itself (src/Elio.Core/UpdateTarget.cs): it is told about a new
# release, and the new AppImage is downloaded like the first one.
set -eu

VERSION=$1
RID=$2
PUBLISH=$3
OUT=$4

# https://github.com/AppImage/appimagetool/releases/tag/1.9.1
APPIMAGETOOL_VERSION=1.9.1
# The program at the start of every AppImage, which mounts the image and starts what is in it.
# https://github.com/AppImage/type2-runtime/releases/tag/20251108
RUNTIME_VERSION=20251108

case "$RID" in
    linux-x64) ARCH=x86_64; RUNTIME_SHA256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    linux-arm64) ARCH=aarch64; RUNTIME_SHA256=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) echo "Unknown runtime: $RID" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64) TOOL_ARCH=x86_64; TOOL_SHA256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
    aarch64) TOOL_ARCH=aarch64; TOOL_SHA256=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 ;;
    *) echo "There is no appimagetool for this computer: $(uname -m)" >&2; exit 1 ;;
esac

ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOWNLOADS=$ROOT/native/downloads
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$DOWNLOADS"

fetch() {
    [ -f "$2" ] || curl -fsSL --retry 3 -o "$2" "$1"
}

check() {
    [ "$(sha256sum "$2" | cut -d' ' -f1)" = "$1" ] || { echo "Checksum mismatch: $2" >&2; exit 1; }
}

TOOL=$DOWNLOADS/appimagetool-$APPIMAGETOOL_VERSION-$TOOL_ARCH.AppImage
fetch "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-$TOOL_ARCH.AppImage" "$TOOL"
check "$TOOL_SHA256" "$TOOL"

RUNTIME=$DOWNLOADS/appimage-runtime-$RUNTIME_VERSION-$ARCH
fetch "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-$ARCH" "$RUNTIME"
check "$RUNTIME_SHA256" "$RUNTIME"

# Unpacked into squashfs-root, appimagetool runs without mounting itself, which containers and some CI machines cannot do.
cp "$TOOL" "$WORK/appimagetool.AppImage"
chmod 755 "$WORK/appimagetool.AppImage"
(cd "$WORK" && ./appimagetool.AppImage --appimage-extract > /dev/null)
APPIMAGETOOL=$WORK/squashfs-root/AppRun

APPDIR=$WORK/Elio.AppDir
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/scalable/apps" \
    "$APPDIR/usr/share/icons/hicolor/512x512/apps" "$APPDIR/usr/share/doc/elio"

install -m 755 "$PUBLISH/elio" "$APPDIR/usr/bin/elio"
install -m 644 "$ROOT/web/Elio.Web/wwwroot/favicon.svg" "$APPDIR/usr/share/icons/hicolor/scalable/apps/elio.svg"
install -m 644 "$ROOT/web/Elio.Web/wwwroot/img/icon-512.png" "$APPDIR/usr/share/icons/hicolor/512x512/apps/elio.png"
install -m 644 "$ROOT/LICENSE" "$APPDIR/usr/share/doc/elio/LICENSE"
install -m 644 "$ROOT/NOTICE" "$APPDIR/usr/share/doc/elio/NOTICE"

# The same menu entry as the .deb's.
cat > "$APPDIR/usr/share/applications/elio.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Elio
GenericName=Terminal and SSH client
Comment=Terminal and SSH client
Exec=elio
Icon=elio
Terminal=false
Categories=System;TerminalEmulator;Network;
Keywords=ssh;sftp;terminal;shell;
StartupWMClass=elio
EOF

# What an AppImage has at the top: the menu entry, its icon (a PNG, as .DirIcon too, which is the picture file
# managers show for the file), and AppRun, which the runtime starts.
cp "$APPDIR/usr/share/applications/elio.desktop" "$APPDIR/elio.desktop"
cp "$APPDIR/usr/share/icons/hicolor/512x512/apps/elio.png" "$APPDIR/elio.png"
cp "$APPDIR/elio.png" "$APPDIR/.DirIcon"
ln -s usr/bin/elio "$APPDIR/AppRun"

# ARCH names the processor the image is for, which is not this computer's when linux-arm64 is packed on x86_64.
# VERSION is written into the image's menu entry. No AppStream file is shipped, so none is looked for.
# The image is made in the work folder and moved out only when it is whole.
NAME=elio-$VERSION-$RID.AppImage
ARCH=$ARCH VERSION=$VERSION "$APPIMAGETOOL" --no-appstream --runtime-file "$RUNTIME" "$APPDIR" "$WORK/$NAME"
chmod 755 "$WORK/$NAME"
mkdir -p "$OUT"
mv "$WORK/$NAME" "$OUT/$NAME"
ls -l "$OUT/$NAME"
