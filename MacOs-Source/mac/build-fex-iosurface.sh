#!/usr/bin/env bash
# Uso: bash mac/build-fex-iosurface.sh <MetalSharp-Wine/src/wine> <llvm-mingw>
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="$root/dist/mac/runtime-arm64"
base="$runtime/MetalSharp-Wine.app"
output="$runtime/MetalSharp-Wine-IOSurface.app"
revision=702b230e88d538a2fa77bc3541726fcd7ada8f74  # MetalSharp Wine v6.0

fail() { printf '%s\n' "$*" >&2; exit 1; }
[[ $# == 2 ]] || fail "Uso: bash mac/build-fex-iosurface.sh <MetalSharp-Wine/src/wine> <llvm-mingw>"
src="$(cd "$1" && pwd -P)"
mingw="$(cd "$2" && pwd -P)"
[[ "$(git -C "$src" rev-parse HEAD)" == "$revision" ]] || fail "Serve il sorgente Wine del tag MetalSharp Wine v6.0."
[[ -x "$mingw/bin/aarch64-w64-mingw32-clang" ]] || fail "Serve llvm-mingw per macOS con supporto ARM64EC."
[[ -x "$base/Contents/MacOS/wine" ]] || fail "Runtime MetalSharp Wine ARM64 assente: $base"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$base/Contents/Info.plist")" == 6.0 &&
   "$(/usr/bin/lipo -archs "$base/Contents/MacOS/wine")" == arm64 ]] || fail "Serve MetalSharp Wine v6.0 ARM64."
[[ ! -e "$output" ]] || fail "Il runtime IOSurface è già presente: $output"

work="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-wine-fex.XXXXXX")"
stage=""
trap 'rm -rf -- "$work"; if [[ -n "$stage" ]]; then rm -rf -- "$stage"; fi' EXIT
stage="$(mktemp -d "$runtime/.iosurface-build.XXXXXX")"
git clone --quiet --shared --no-checkout "$src" "$work/wine"
git -C "$work/wine" checkout --quiet --detach "$revision"
git -C "$work/wine" apply --reject \
    "$root/mac/wine-patches/0003-wined3d-cinecore-iosurface-zero-copy.patch"
git -C "$work/wine" apply \
    "$root/mac/wine-patches/0005-wined3d-cinecore-download-handshake.patch"
git -C "$work/wine" apply \
    "$root/mac/wine-patches/0010-wined3d-fex-iosurface.patch" \
    "$root/mac/wine-patches/0011-winemac-fex-cinecore-fullscreen.patch" \
    "$root/mac/wine-patches/0012-winemac-opengl-context-dealloc.patch"
git -C "$work/wine" diff --check

export PATH="$mingw/bin:/opt/homebrew/opt/bison/bin:/opt/homebrew/bin:/usr/bin:/bin"
export SDKROOT="$(xcrun --show-sdk-path)" MACOSX_DEPLOYMENT_TARGET=15.0
export CC='/usr/bin/clang -arch arm64' OBJC='/usr/bin/clang -arch arm64'
export CFLAGS='-O2 -mmacosx-version-min=15.0' OBJCFLAGS='-O2 -mmacosx-version-min=15.0'
build="$work/build"
mkdir "$build"
(
    cd "$build"
    "$work/wine/configure" --enable-archs=arm64ec,aarch64 --with-mingw=llvm-mingw --with-opengl \
        --without-vulkan --without-x --without-wayland --without-freetype --without-gnutls \
        --without-gstreamer --without-ffmpeg --without-sdl --without-cups --without-sane \
        --without-krb5 --without-netapi --without-pcap --without-usb --without-v4l2 \
        --without-pulse --without-alsa --without-oss --without-gphoto --without-capi \
        --without-dbus --without-inotify --without-opencl --without-gettext --without-fontconfig \
        --without-pcsclite --without-udev --without-xinerama --disable-tests
) >"$stage/configure.log" 2>&1 || { tail -60 "$stage/configure.log" >&2; exit 1; }
make -C "$build" -j"${CINECORE_FEX_BUILD_JOBS:-3}" \
    dlls/wined3d/aarch64-windows/wined3d.dll dlls/wined3d/wined3d.so dlls/winemac.drv/winemac.so \
    >"$stage/build.log" 2>&1 || { tail -60 "$stage/build.log" >&2; exit 1; }

bundle="$stage/MetalSharp-Wine-IOSurface.app"
cp -cR "$base" "$bundle"
lib="$bundle/Contents/lib/wine"
cp "$build/dlls/wined3d/aarch64-windows/wined3d.dll" "$lib/aarch64-windows/wined3d.dll"
cp "$build/dlls/wined3d/wined3d.so" "$lib/aarch64-unix/wined3d.so"
cp "$build/dlls/winemac.drv/winemac.so" "$lib/aarch64-unix/winemac.so"
/usr/bin/codesign --force --sign - "$lib/aarch64-unix/wined3d.so" "$lib/aarch64-unix/winemac.so"
/usr/bin/codesign --force --deep --sign - "$bundle"
/usr/bin/codesign --verify --deep --strict "$bundle"
[[ ! -e "$output" ]] || fail "Il runtime IOSurface è già presente: $output"
mv "$bundle" "$output"
printf 'Creato %s\n' "$output"
