#!/usr/bin/env bash
# Prepara il runtime Wine per macOS usato da Cinecore Player:
#   - Wine 11 "Sikarugir" (x86_64, gira con Rosetta 2): espone le funzioni di winemac che servono a DXMT;
#   - le librerie di sistema di Wine (freetype, gnutls, MoltenVK, SDL2...) dalla build Gcenx;
#   - DXMT: Direct3D 10/11 -> Metal (MPC Video Renderer, mpv d3d11, madVR in D3D11).
#   Direct3D 9 (madVR, WPF) resta a wined3d, il Direct3D di Wine (OpenGL o Vulkan/MoltenVK).
#
# Uso:  bash mac/build-runtime.sh [DEST]      (predefinito: dist/mac/runtime)
# Il risultato e' DEST/wine/{bin,lib,share}. I download restano in ~/Library/Caches/Cinecore/downloads.
set -euo pipefail

ENGINE_URL="https://github.com/Sikarugir-App/Engines/releases/download/v1.0/WS12WineSikarugir11.0_1.tar.xz"
LIBS_URL="https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.21/wine-staging-11.21-osx64.tar.xz"
DXMT_URL="https://github.com/3Shain/dxmt/releases/download/v0.80/dxmt-v0.80-builtin.tar.gz"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="${1:-$HERE/../dist/mac/runtime}"
CACHE="${CINECORE_DL_CACHE:-$HOME/Library/Caches/Cinecore/downloads}"
mkdir -p "$CACHE" "$DEST"
DEST="$(cd "$DEST" && pwd)"

say() { printf '\033[1m==>\033[0m %s\n' "$*"; }
fetch() { local f="$CACHE/$(basename "$1")"; [[ -s "$f" ]] || { say "Scarico $(basename "$1")"; curl -fL --retry 3 -o "$f.part" "$1" && mv "$f.part" "$f"; }; echo "$f"; }

ENGINE_TAR="$(fetch "$ENGINE_URL")"
LIBS_TAR="$(fetch "$LIBS_URL")"
DXMT_TAR="$(fetch "$DXMT_URL")"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-rt.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

say "Estraggo Wine"
mkdir -p "$WORK/engine" "$WORK/libs" "$WORK/dxmt"
tar -xf "$ENGINE_TAR" -C "$WORK/engine"
tar -xf "$LIBS_TAR" -C "$WORK/libs"
tar -xzf "$DXMT_TAR" -C "$WORK/dxmt"

ENGINE="$(find "$WORK/engine" -maxdepth 2 -name wswine.bundle -type d | head -1)"
LIBSRC="$(find "$WORK/libs" -path '*Contents/Resources/wine/lib' -type d | head -1)"
DXMT="$(find "$WORK/dxmt" -maxdepth 2 -name 'v*' -type d | head -1)"
[[ -d "$ENGINE" && -d "$LIBSRC" && -d "$DXMT" ]] || { echo "Archivi non riconosciuti" >&2; exit 1; }
nm -gU "$ENGINE/lib/wine/x86_64-unix/winemac.so" | grep -q _macdrv_functions || { echo "Questo Wine non espone macdrv_functions: DXMT non funzionerebbe." >&2; exit 1; }

say "Assemblo il runtime in $DEST/wine"
rm -rf "$DEST/wine"
mkdir -p "$DEST/wine"
cp -R "$ENGINE/bin" "$ENGINE/lib" "$ENGINE/share" "$DEST/wine/"
# Librerie caricate da Wine con dlopen (freetype per i caratteri, gnutls per HTTPS/crypto, MoltenVK per Vulkan).
# GStreamer non serve: i decoder sono LAV Filters, ffmpeg e mpv.
find "$LIBSRC" -maxdepth 1 -name '*.dylib' -exec cp -P {} "$DEST/wine/lib/" \;

say "Installo DXMT (Direct3D 11 -> Metal)"
W="$DEST/wine/lib/wine"
cp "$DXMT/x86_64-unix/winemetal.so" "$W/x86_64-unix/"
for d in winemetal d3d11 dxgi d3d10core; do
  cp "$DXMT/x86_64-windows/$d.dll" "$W/x86_64-windows/"
  [[ -f "$DXMT/i386-windows/$d.dll" ]] && cp "$DXMT/i386-windows/$d.dll" "$W/i386-windows/"
done
# Copia per il prefisso: setup-prefix.sh la mette in system32 (lo chiede DXMT).
mkdir -p "$DEST/wine/share/cinecore/dxmt"
cp "$DXMT/x86_64-windows/winemetal.dll" "$DEST/wine/share/cinecore/dxmt/winemetal64.dll"
[[ -f "$DXMT/i386-windows/winemetal.dll" ]] && cp "$DXMT/i386-windows/winemetal.dll" "$DEST/wine/share/cinecore/dxmt/winemetal32.dll"

# Decoder DirectShow (LAV, MPC Audio) estratti dagli installer: sotto Wine per macOS l'installer non parte.
INST="$HERE/../third-parties/installers"
if command -v innoextract >/dev/null; then
  say "Estraggo LAV Filters e MPC Audio"
  for f in LAVFilters-0.80 MPC-Audio-Decoder-x64; do
    [[ -f "$INST/$f.exe" ]] || continue
    mkdir -p "$DEST/wine/share/cinecore/decoders/$f"
    innoextract -s -d "$DEST/wine/share/cinecore/decoders/$f" "$INST/$f.exe"
    rm -rf "$DEST/wine/share/cinecore/decoders/$f/tmp" "$DEST/wine/share/cinecore/decoders/$f/app/x86"
  done
else
  echo "!! innoextract non trovato (brew install innoextract): i decoder LAV/MPC Audio non saranno nel runtime." >&2
fi

xattr -dr com.apple.quarantine "$DEST/wine" 2>/dev/null || true
{
  echo "engine=$(head -1 "$ENGINE/version" 2>/dev/null)"
  echo "libs=$(basename "$LIBS_URL")"
  echo "dxmt=$(basename "$DXMT")"
} > "$DEST/wine/share/cinecore/runtime-version"
say "Runtime pronto: $(du -sh "$DEST/wine" | cut -f1)"
