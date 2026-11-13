#!/bin/sh
# Writes update.json for a release and signs it. Run by the release workflow, on the folder of files it is about
# to publish:
#
#   sh build/update-manifest.sh <version> <dist-dir> <private-key-file>
#
# <version> is three numbers, such as 1.2.3, and <dist-dir> holds the release's files under the names without a
# version (elio-win-x64.zip and so on). update.json lists the version and the SHA-256 and size of each file a
# copy of Elio can install itself from; update.json.sig is its signature, made with the private key. A copy of
# Elio installs nothing that this signature does not cover. Needs openssl.
set -eu

VERSION=${1:-}
DIST=${2:-}
KEY=${3:-}
if [ -z "$VERSION" ] || [ -z "$DIST" ] || [ -z "$KEY" ]; then
    echo "Usage: sh build/update-manifest.sh <version> <dist-dir> <private-key-file>" >&2
    exit 1
fi

# Only a plain version: a pre-release is announced to nobody and installed by nobody.
case "$VERSION" in
    *[!0-9.]* | .* | *. | *..*) echo "'$VERSION' is not a version of three numbers." >&2; exit 1 ;;
esac
if [ "$(printf '%s' "$VERSION" | tr -cd '.' | wc -c | tr -d ' ')" != 2 ]; then
    echo "'$VERSION' is not a version of three numbers." >&2
    exit 1
fi

ROOT=$(cd "$(dirname "$0")/.." && pwd)
KEYS=${ELIO_UPDATE_KEYS_FILE:-$ROOT/src/Elio.Core/update-keys.txt}

hash_of() {
    if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

MANIFEST=$DIST/update.json
printf '{"version":"%s","files":{' "$VERSION" > "$MANIFEST"
SEPARATOR=
COUNT=0
for NAME in elio-win-x64.zip elio-osx-arm64.zip elio-osx-x64.zip elio-linux-x64.tar.gz elio-linux-arm64.tar.gz; do
    if [ ! -f "$DIST/$NAME" ]; then
        echo "Note: $NAME is not in $DIST, so copies for that system will not update themselves from this release." >&2
        continue
    fi
    printf '%s"%s":{"sha256":"%s","size":%s}' "$SEPARATOR" "$NAME" "$(hash_of "$DIST/$NAME")" "$(wc -c < "$DIST/$NAME" | tr -d ' ')" >> "$MANIFEST"
    SEPARATOR=,
    COUNT=$((COUNT + 1))
done
printf '}}\n' >> "$MANIFEST"

if [ "$COUNT" = 0 ]; then
    echo "There is nothing to list in $DIST." >&2
    rm -f "$MANIFEST"
    exit 1
fi

openssl dgst -sha256 -sign "$KEY" -out "$DIST/update.json.sig" "$MANIFEST"

# The program that was built into the release trusts the keys in update-keys.txt. A signature that none of them
# accepts would make a release that no copy can install, so that is found out here and not by the people who use it.
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
VALID=0
for LINE in $(grep -v '^#' "$KEYS" | tr -d '\r'); do
    printf '%s' "$LINE" | base64 -d > "$WORK/public.der" 2>/dev/null || continue
    openssl pkey -pubin -inform DER -in "$WORK/public.der" -out "$WORK/public.pem" 2>/dev/null || continue
    if openssl dgst -sha256 -verify "$WORK/public.pem" -signature "$DIST/update.json.sig" "$MANIFEST" > /dev/null 2>&1; then VALID=1; fi
done
if [ "$VALID" != 1 ]; then
    rm -f "$MANIFEST" "$DIST/update.json.sig"
    echo "The signature is not accepted by any key in $KEYS. The private key does not belong to a key built into this version." >&2
    exit 1
fi

echo "Signed $MANIFEST ($COUNT files)."
cat "$MANIFEST"
