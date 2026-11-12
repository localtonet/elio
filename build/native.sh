#!/bin/sh
# Builds libghostty-vt for the platforms Elio ships on and leaves the libraries in native/out/<rid>.
# Run it after cloning, and again when the pinned versions below change.
#
# On Linux:             sh build/native.sh [rid...]
# From any other OS:    docker run --rm -v "$PWD:/src" -w /src ruby:3.3 sh build/native.sh [rid...]
#
# It builds on Linux because installing the library creates symlinks, which Windows refuses by default.
# Android targets need the Linux NDK in ANDROID_NDK_HOME.
# Apple platforms use the official XCFramework instead, which has to be built on a Mac.
set -eu

ZIG_VERSION=0.17.1
ZIG_SHA256=3581863e3cbcfd0fce0efc682ce7e9e93da96668a977791d356a26d2a7e9b17a
GHOSTTY_VERSION=1.0.0

ROOT=$(pwd)/native
DOWNLOADS=$ROOT/downloads
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$DOWNLOADS" "$ROOT/out"

fetch() {
    [ -f "$2" ] || curl -fsSL --retry 3 -o "$2" "$1"
}

ZIG_NAME=zig-x86_64-linux-$ZIG_VERSION
fetch "https://ziglang.org/download/$ZIG_VERSION/$ZIG_NAME.tar.xz" "$DOWNLOADS/$ZIG_NAME.tar.xz"
echo "$ZIG_SHA256  $DOWNLOADS/$ZIG_NAME.tar.xz" | sha256sum -c - >/dev/null
tar -xJf "$DOWNLOADS/$ZIG_NAME.tar.xz" -C "$WORK"
ZIG=$WORK/$ZIG_NAME/zig

# The source and its dependencies must come from the same release.
RELEASE=https://release.files.ghostty.org/libghostty-vt/$GHOSTTY_VERSION
SOURCE=libghostty-vt-$GHOSTTY_VERSION
fetch "$RELEASE/SHA256SUMS" "$DOWNLOADS/SHA256SUMS-$GHOSTTY_VERSION"
for name in "$SOURCE.tar.gz" "$SOURCE-deps.tar.gz"; do
    fetch "$RELEASE/$name" "$DOWNLOADS/$name"
    expected=$(grep " $name\$" "$DOWNLOADS/SHA256SUMS-$GHOSTTY_VERSION" | cut -d' ' -f1)
    echo "$expected  $DOWNLOADS/$name" | sha256sum -c - >/dev/null
done
tar -xzf "$DOWNLOADS/$SOURCE.tar.gz" -C "$WORK"
mkdir "$WORK/deps"
tar -xzf "$DOWNLOADS/$SOURCE-deps.tar.gz" -C "$WORK/deps"
DEPS=$(find "$WORK/deps" -mindepth 1 -maxdepth 1 -type d | head -1)

target_of() {
    case "$1" in
        win-x64) echo x86_64-windows-gnu ;;
        linux-x64) echo x86_64-linux-gnu.2.28 ;;
        linux-arm64) echo aarch64-linux-gnu.2.28 ;;
        android-arm64) echo aarch64-linux-android.24 ;;
        android-x64) echo x86_64-linux-android.24 ;;
        android-arm) echo arm-linux-androideabi.24 ;;
        *) echo "Unknown target: $1" >&2; exit 1 ;;
    esac
}

[ $# -gt 0 ] || set -- win-x64 linux-x64 linux-arm64 android-arm64 android-arm android-x64

failed=""
for rid in "$@"; do
    echo "== $rid"
    prefix=$WORK/out/$rid

    # A reused cache can crash Zig 0.17, so each target starts from an empty one.
    if ! (cd "$WORK/$SOURCE" && ZIG_GLOBAL_CACHE_DIR=$WORK/cache/$rid "$ZIG" build \
        -Doptimize=ReleaseFast -Dcpu=baseline -Dstrip=true "-Dtarget=$(target_of "$rid")" \
        --prefix "$prefix" --system "$DEPS"); then
        failed="$failed $rid"
        continue
    fi

    out=$ROOT/out/$rid
    rm -rf "$out"
    if [ "$rid" = win-x64 ]; then
        mkdir -p "$out/bin"
        cp "$prefix/bin/ghostty-vt.dll" "$out/bin/"
    else
        mkdir -p "$out/lib"
        cp -L "$prefix/lib/libghostty-vt.so" "$out/lib/libghostty-vt.so"
    fi
    ls -l "$out"/*/
done

[ -z "$failed" ] || { echo "Failed:$failed" >&2; exit 1; }
