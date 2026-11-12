#!/bin/sh
# Builds libghostty-vt for macOS and leaves the libraries in native/out/<rid>/lib. Run it on a Mac:
#
#   sh build/native-apple.sh [osx-arm64] [osx-x64] [ios]
#
# It is the Mac's half of build/native.sh: the same pinned versions, every download checked against its
# SHA-256, and the same build options. Linking for Apple's systems needs their SDK, which is why this part
# cannot be done from Linux with the rest. Needs the Xcode command line tools.
set -eu

ZIG_VERSION=0.17.1
GHOSTTY_VERSION=1.0.0

case "$(uname -m)" in
    arm64) ZIG_HOST=aarch64-macos; ZIG_SHA256=75f1bc7a1e9852f17ec9a0c1895f767c4ec56d46bc8a834920eece1d0359dd8d ;;
    x86_64) ZIG_HOST=x86_64-macos; ZIG_SHA256=9af07b20319fbd0c92ee46ad23e81d6a782b1aaea2cfb8dcbff35f1d2c7ddbb7 ;;
    *) echo "Unknown Mac: $(uname -m)" >&2; exit 1 ;;
esac

ROOT=$(pwd)/native
DOWNLOADS=$ROOT/downloads
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$DOWNLOADS" "$ROOT/out"

fetch() {
    [ -f "$2" ] || curl -fsSL --retry 3 -o "$2" "$1"
}

check() {
    [ "$(shasum -a 256 "$2" | cut -d' ' -f1)" = "$1" ] || { echo "Checksum mismatch: $2" >&2; exit 1; }
}

ZIG_NAME=zig-$ZIG_HOST-$ZIG_VERSION
fetch "https://ziglang.org/download/$ZIG_VERSION/$ZIG_NAME.tar.xz" "$DOWNLOADS/$ZIG_NAME.tar.xz"
check "$ZIG_SHA256" "$DOWNLOADS/$ZIG_NAME.tar.xz"
tar -xJf "$DOWNLOADS/$ZIG_NAME.tar.xz" -C "$WORK"
ZIG=$WORK/$ZIG_NAME/zig

# The source and its dependencies must come from the same release.
RELEASE=https://release.files.ghostty.org/libghostty-vt/$GHOSTTY_VERSION
SOURCE=libghostty-vt-$GHOSTTY_VERSION
fetch "$RELEASE/SHA256SUMS" "$DOWNLOADS/SHA256SUMS-$GHOSTTY_VERSION"
for name in "$SOURCE.tar.gz" "$SOURCE-deps.tar.gz"; do
    fetch "$RELEASE/$name" "$DOWNLOADS/$name"
    check "$(grep " $name\$" "$DOWNLOADS/SHA256SUMS-$GHOSTTY_VERSION" | cut -d' ' -f1)" "$DOWNLOADS/$name"
done
tar -xzf "$DOWNLOADS/$SOURCE.tar.gz" -C "$WORK"
mkdir "$WORK/deps"
tar -xzf "$DOWNLOADS/$SOURCE-deps.tar.gz" -C "$WORK/deps"
DEPS=$(find "$WORK/deps" -mindepth 1 -maxdepth 1 -type d | head -1)

target_of() {
    case "$1" in
        osx-arm64) echo aarch64-macos ;;
        osx-x64) echo x86_64-macos ;;
        *) echo "Unknown target: $1" >&2; exit 1 ;;
    esac
}

[ $# -gt 0 ] || set -- osx-arm64 osx-x64 ios

failed=""
for rid in "$@"; do
    echo "== $rid"
    prefix=$WORK/out/$rid

    # An iPhone app links the engine into itself, so for iOS it is built as static libraries in an XCFramework,
    # with one for a device and one for the simulator.
    if [ "$rid" = ios ]; then
        if ! (cd "$WORK/$SOURCE" && ZIG_GLOBAL_CACHE_DIR=$WORK/cache/$rid "$ZIG" build \
            -Doptimize=ReleaseFast -Dstrip=true -Demit-lib-vt=true -Demit-xcframework=true -Dxcframework-target=universal \
            --prefix "$prefix" --system "$DEPS"); then
            failed="$failed $rid"
            continue
        fi

        rm -rf "$ROOT/out/ios"
        mkdir -p "$ROOT/out/ios"
        cp -R "$prefix/lib/ghostty-vt.xcframework" "$ROOT/out/ios/"
        ls "$ROOT/out/ios/ghostty-vt.xcframework"
        continue
    fi

    # A reused cache can crash Zig 0.17, so each target starts from an empty one.
    if ! (cd "$WORK/$SOURCE" && ZIG_GLOBAL_CACHE_DIR=$WORK/cache/$rid "$ZIG" build \
        -Doptimize=ReleaseFast -Dcpu=baseline -Dstrip=true "-Dtarget=$(target_of "$rid")" \
        --prefix "$prefix" --system "$DEPS"); then
        failed="$failed $rid"
        continue
    fi

    out=$ROOT/out/$rid
    rm -rf "$out"
    mkdir -p "$out/lib"
    cp -L "$prefix/lib/libghostty-vt.dylib" "$out/lib/libghostty-vt.dylib"
    ls -l "$out/lib/"
done

[ -z "$failed" ] || { echo "Failed:$failed" >&2; exit 1; }
