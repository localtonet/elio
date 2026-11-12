#!/bin/sh
# Wraps a published Linux build of Elio in a .deb, with a menu entry and an icon.
#
#   sh build/package-deb.sh <version> <rid> <publish-dir> <out-dir>
#
# <rid> is linux-x64 or linux-arm64, and <publish-dir> holds the single file "elio" that
# "dotnet publish src/Elio.Desktop -c Release -r <rid>" leaves. Needs dpkg-deb.
set -eu

VERSION=$1
RID=$2
PUBLISH=$3
OUT=$4

case "$RID" in
    linux-x64) ARCH=amd64 ;;
    linux-arm64) ARCH=arm64 ;;
    *) echo "Unknown runtime: $RID" >&2; exit 1 ;;
esac

# A Debian version starts with a digit and has no hyphen in its upstream part: 1.2.3-beta.1 becomes 1.2.3~beta.1,
# which also sorts before 1.2.3 as a pre-release should.
DEB_VERSION=$(echo "$VERSION" | sed 's/-/~/')

ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
PKG=$WORK/elio_${DEB_VERSION}_$ARCH

mkdir -p "$PKG/DEBIAN" "$PKG/usr/bin" "$PKG/usr/share/applications" \
    "$PKG/usr/share/icons/hicolor/scalable/apps" "$PKG/usr/share/doc/elio"

install -m 755 "$PUBLISH/elio" "$PKG/usr/bin/elio"
install -m 644 "$ROOT/web/Elio.Web/wwwroot/favicon.svg" "$PKG/usr/share/icons/hicolor/scalable/apps/elio.svg"
install -m 644 "$ROOT/LICENSE" "$PKG/usr/share/doc/elio/copyright"
install -m 644 "$ROOT/NOTICE" "$PKG/usr/share/doc/elio/NOTICE"

cat > "$PKG/usr/share/applications/elio.desktop" <<EOF
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

# The file brings .NET with it, but not the libraries every desktop has: the C library, fonts, X11, and ICU,
# whose package is named after its version and so differs from one release of a distribution to the next.
cat > "$PKG/DEBIAN/control" <<EOF
Package: elio
Version: $DEB_VERSION
Architecture: $ARCH
Maintainer: Localtonet <localtonet@users.noreply.github.com>
Section: net
Priority: optional
Homepage: https://elio.sh
Installed-Size: $(du -sk "$PKG/usr" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6, libicu78 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66
Description: Terminal and SSH client
 Elio is a terminal and SSH client with tabs and split panes, SFTP, port
 forwarding, an encrypted vault for passwords and keys, and end-to-end
 encrypted sync between devices.
EOF

mkdir -p "$OUT"
dpkg-deb --build --root-owner-group "$PKG" "$OUT/elio-$VERSION-$RID.deb" > /dev/null
ls -l "$OUT/elio-$VERSION-$RID.deb"
