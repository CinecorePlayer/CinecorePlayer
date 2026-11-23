#!/usr/bin/env bash
# Uso: bash mac/build-videotoolbox-bridge.sh [sorgenti FFmpeg di LAV 0.80]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="$root/dist/mac/runtime-arm64"
revision=433869d8d62e6d2cbfac298053e4e494dd6c755b
build="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-vt-build.XXXXXX")"
trap 'rm -rf -- "$build"' EXIT
ffmpeg="${1:-$build/ffmpeg}"
if [[ $# -eq 0 ]]; then
    git init -q "$ffmpeg"
    git -C "$ffmpeg" fetch --depth=1 https://gitea.1f0.de/LAV/FFmpeg.git "$revision"
    git -C "$ffmpeg" checkout -q --detach FETCH_HEAD
fi
[[ "$(git -C "$ffmpeg" rev-parse HEAD)" == "$revision" ]] || {
    echo "Servono le intestazioni FFmpeg del commit $revision (LAV 0.80)." >&2
    exit 1
}
mkdir -p "$runtime" "$build/include/libavutil"
cat >"$build/include/libavutil/avconfig.h" <<'EOF'
#ifndef AVUTIL_AVCONFIG_H
#define AVUTIL_AVCONFIG_H
#define AV_HAVE_BIGENDIAN 0
#define AV_HAVE_FAST_UNALIGNED 1
#endif
EOF
x86_64-w64-mingw32-gcc -std=c11 -O2 -Wall -Wextra -Werror -shared \
    -I"$build/include" -I"$ffmpeg" "$root/mac-native/VideoToolboxBridge/vt_windows.c" \
    -lws2_32 -o "$build/CinecoreVideoToolbox.dll"
mv -f "$build/CinecoreVideoToolbox.dll" "$runtime/"
