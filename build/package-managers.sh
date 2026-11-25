#!/bin/sh
# Writes the files that put a release of Elio into winget, Homebrew and Scoop. Run by the release workflow on the
# folder of files it published (under the names without a version, such as elio-win-x64.zip):
#
#   sh build/package-managers.sh <version> <dist-dir> <out-dir>
#
# <version> is a plain release version such as 1.2.3. It leaves, for each system whose file is in <dist-dir>:
#   <out-dir>/winget/  the three manifests for microsoft/winget-pkgs (Localtonet.Elio)
#   <out-dir>/scoop/elio.json  the manifest for the Scoop bucket
#   <out-dir>/homebrew/Casks/elio.rb  the cask for the Homebrew tap
# A system whose build failed has no file and so gets no manifest. The files are checked by whoever installs them
# against the SHA-256 written here, which is the one of the file on the release.
set -eu

VERSION=${1:-}
DIST=${2:-}
OUT=${3:-}
if [ -z "$VERSION" ] || [ -z "$DIST" ] || [ -z "$OUT" ]; then
    echo "Usage: sh build/package-managers.sh <version> <dist-dir> <out-dir>" >&2
    exit 1
fi
case "$VERSION" in
    *[!0-9.]* | .* | *. | *..*) echo "'$VERSION' is not a plain version." >&2; exit 1 ;;
esac

BASE=https://github.com/localtonet/elio/releases/download/v$VERSION
HOMEPAGE=https://elio.sh
DESCRIPTION="Terminal and SSH client"
TODAY=$(date -u +%Y-%m-%d)

hash_of() {
    if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

mkdir -p "$OUT"

# ---- Windows: winget and Scoop, with each Windows build the release has (x64 and ARM64). Each zip holds
# elio-<version>-<rid>/elio.exe, so every build names its own folder.
WIN_RIDS=
for RID in win-x64 win-arm64; do
    if [ -f "$DIST/elio-$RID.zip" ]; then WIN_RIDS="$WIN_RIDS $RID"; fi
done

# What winget and Scoop call each processor.
winget_arch() { case "$1" in win-x64) echo x64 ;; win-arm64) echo arm64 ;; esac; }
scoop_arch() { case "$1" in win-x64) echo 64bit ;; win-arm64) echo arm64 ;; esac; }

if [ -n "$WIN_RIDS" ]; then
    W=$OUT/winget
    mkdir -p "$W"
    cat > "$W/Localtonet.Elio.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: Localtonet.Elio
PackageVersion: $VERSION
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
EOF
    cat > "$W/Localtonet.Elio.installer.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: Localtonet.Elio
PackageVersion: $VERSION
InstallerType: zip
NestedInstallerType: portable
Commands:
- elio
Installers:
EOF
    for RID in $WIN_RIDS; do
        cat >> "$W/Localtonet.Elio.installer.yaml" <<EOF
- Architecture: $(winget_arch "$RID")
  NestedInstallerFiles:
  - RelativeFilePath: elio-$VERSION-$RID\\elio.exe
    PortableCommandAlias: elio
  InstallerUrl: $BASE/elio-$RID.zip
  InstallerSha256: $(hash_of "$DIST/elio-$RID.zip" | tr 'a-f' 'A-F')
EOF
    done
    cat >> "$W/Localtonet.Elio.installer.yaml" <<EOF
ReleaseDate: $TODAY
ManifestType: installer
ManifestVersion: 1.6.0
EOF
    cat > "$W/Localtonet.Elio.locale.en-US.yaml" <<EOF
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: Localtonet.Elio
PackageVersion: $VERSION
PackageLocale: en-US
Publisher: Localtonet
PublisherUrl: https://localtonet.com
PublisherSupportUrl: https://github.com/localtonet/elio/issues
PackageName: Elio
PackageUrl: $HOMEPAGE
License: Apache-2.0
LicenseUrl: https://github.com/localtonet/elio/blob/main/LICENSE
ShortDescription: $DESCRIPTION
Description: A fast, free and open-source terminal and SSH client with tabs and split panes, SFTP, port forwarding, an encrypted vault and end-to-end encrypted sync.
Moniker: elio
Tags:
- ssh
- sftp
- terminal
- telnet
- serial
ReleaseNotesUrl: https://github.com/localtonet/elio/releases/tag/v$VERSION
ManifestType: defaultLocale
ManifestVersion: 1.6.0
EOF

    mkdir -p "$OUT/scoop"
    cat > "$OUT/scoop/elio.json" <<EOF
{
    "version": "$VERSION",
    "description": "$DESCRIPTION",
    "homepage": "$HOMEPAGE",
    "license": "Apache-2.0",
    "architecture": {
EOF
    SEPARATOR=
    for RID in $WIN_RIDS; do
        printf '%s        "%s": {\n            "url": "%s",\n            "hash": "%s",\n            "extract_dir": "%s"\n        }' \
            "$SEPARATOR" "$(scoop_arch "$RID")" "$BASE/elio-$RID.zip" "$(hash_of "$DIST/elio-$RID.zip")" "elio-$VERSION-$RID" >> "$OUT/scoop/elio.json"
        SEPARATOR=",
"
    done
    cat >> "$OUT/scoop/elio.json" <<EOF

    },
    "bin": "elio.exe",
    "shortcuts": [
        ["elio.exe", "Elio"]
    ]
}
EOF
fi

# ---- macOS: a cask. The zip holds Elio.app, which updates itself (auto_updates), so brew leaves upgrading to it.
if [ -f "$DIST/elio-osx-arm64.zip" ] && [ -f "$DIST/elio-osx-x64.zip" ]; then
    mkdir -p "$OUT/homebrew/Casks"
    cat > "$OUT/homebrew/Casks/elio.rb" <<EOF
cask "elio" do
  arch arm: "arm64", intel: "x64"

  version "$VERSION"
  sha256 arm:   "$(hash_of "$DIST/elio-osx-arm64.zip")",
         intel: "$(hash_of "$DIST/elio-osx-x64.zip")"

  url "https://github.com/localtonet/elio/releases/download/v#{version}/elio-osx-#{arch}.zip"
  name "Elio"
  desc "$DESCRIPTION"
  homepage "$HOMEPAGE"

  livecheck do
    url :url
    strategy :github_latest
  end

  auto_updates true
  depends_on macos: ">= :big_sur"

  app "Elio.app"
end
EOF
fi

find "$OUT" -type f | sort
