#!/usr/bin/env bash
# Compila winemac.so di Wine Staging 11.21 con la tabella "macdrv_functions" per DXMT e assembla il runtime
# dist/mac/runtime-wine11 = Wine Staging 11.21 (Gcenx) + winemac.so ricompilato + DXMT v0.80.
# Wine >= 10.18 serve a DXMT per le risorse condivise (D3DKMT): madVR le usa fra i suoi dispositivi D3D11.
#
# Prerequisiti: sorgenti in ~/Library/Caches/Cinecore/src (wine-11.21 + patch no-flicker + dxmt_export.c),
# Homebrew: mingw-w64, bison. Uso:  bash mac/build-winemac-dxmt.sh
set -euo pipefail
SRC="$HOME/Library/Caches/Cinecore/src"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GCENX="$ROOT/dist/mac/runtime-gcenx/wine"
DXMT="$HOME/Library/Application Support/Cinecore/runtime/dxmt/v0.80"
OUT="$ROOT/dist/mac/runtime-wine11/wine"
export PATH="/opt/homebrew/opt/bison/bin:/opt/homebrew/bin:$PATH"

[[ -f "$SRC/wine-11.21/dlls/winemac.drv/dxmt_export.c" ]] || { echo "Manca dxmt_export.c nei sorgenti" >&2; exit 1; }
# wined3d Cinecore (lettura asincrona + copia zero GL->Metal via IOSurface): mac/wine-patches/0003 applicata ai sorgenti.
[[ -f "$SRC/wine-11.21/dlls/wined3d/cinecore_mac.m" ]] || { echo "Manca la patch wined3d (mac/wine-patches/0003)" >&2; exit 1; }
grep -q 'cinecore_download_expect' "$SRC/wine-11.21/dlls/wined3d/wined3d.spec" || { echo "Manca l'handshake delle copie (mac/wine-patches/0005)" >&2; exit 1; }
grep -q 'Its borderless video window must accompany' "$SRC/wine-11.21/dlls/winemac.drv/cocoa_window.m" || { echo "Manca la patch fullscreen (mac/wine-patches/0006)" >&2; exit 1; }
grep -q 'The native Cinecore HUD owns mouse input' "$SRC/wine-11.21/dlls/winemac.drv/cocoa_window.m" || { echo "Manca la patch mouse (mac/wine-patches/0007)" >&2; exit 1; }
grep -q 'Place the renderer directly behind the native HUD' "$SRC/wine-11.21/dlls/winemac.drv/cocoa_window.m" || { echo "Manca la patch ordine video (mac/wine-patches/0008)" >&2; exit 1; }
grep -q 'cinecoreVideoBehind' "$SRC/wine-11.21/dlls/winemac.drv/cocoa_window.m" || { echo "Manca la patch transizione fullscreen (mac/wine-patches/0009)" >&2; exit 1; }
grep -q 'wine_super_clear_drawable' "$SRC/wine-11.21/dlls/winemac.drv/cocoa_opengl.m" || { echo "Manca la correzione del contesto OpenGL (mac/wine-patches/0012)" >&2; exit 1; }
mkdir -p "$SRC/build64"; cd "$SRC/build64"
if [[ ! -f Makefile ]]; then
  echo "==> configure (x86_64, solo quello che serve a winemac)"
  arch -x86_64 /bin/bash ../wine-11.21/configure --enable-archs=x86_64 --without-x --without-freetype --without-gnutls \
    --without-gstreamer --without-sdl --without-cups --without-sane --without-krb5 --without-netapi --without-pcap \
    --without-usb --without-v4l2 --without-pulse --without-alsa --without-oss --without-gphoto --without-unwind \
    --without-capi --without-dbus --without-inotify --without-ldap --without-opencl --without-gettext \
    --without-fontconfig --without-pcsclite --without-udev --without-wayland --without-xinerama --disable-tests \
    CC="clang -arch x86_64" OBJC="clang -arch x86_64" > configure.log 2>&1 || { tail -30 configure.log; exit 1; }
fi
echo "==> make winemac.so, wined3d (dll + so), d3d9"
# Senza "arch -x86_64": dal 11/11 gli strumenti di Apple non hanno la parte x86_64 (il Makefile usa clang -arch x86_64).
make -j"$(sysctl -n hw.ncpu)" dlls/winemac.drv/winemac.so dlls/wined3d/x86_64-windows/wined3d.dll \
  dlls/wined3d/wined3d.so dlls/d3d9/x86_64-windows/d3d9.dll > make.log 2>&1 || { tail -40 make.log; exit 1; }
SO="$SRC/build64/dlls/winemac.drv/winemac.so"
nm -gU "$SO" | grep -q _macdrv_functions || { echo "macdrv_functions non esportata" >&2; exit 1; }

echo "==> assemblo $OUT"
rm -rf "$OUT"; mkdir -p "$(dirname "$OUT")"
ditto "$GCENX" "$OUT"
cp "$SO" "$OUT/lib/wine/x86_64-unix/winemac.so"
# d3d9 e wined3d vanno sempre dallo stesso sorgente (Wine Staging cambia l'interfaccia fra i due).
x86_64-w64-mingw32-strip -o "$OUT/lib/wine/x86_64-windows/wined3d.dll" "$SRC/build64/dlls/wined3d/x86_64-windows/wined3d.dll"
x86_64-w64-mingw32-strip -o "$OUT/lib/wine/x86_64-windows/d3d9.dll" "$SRC/build64/dlls/d3d9/x86_64-windows/d3d9.dll"
cp "$SRC/build64/dlls/wined3d/wined3d.so" "$OUT/lib/wine/x86_64-unix/wined3d.so"
W="$OUT/lib/wine"
cp "$DXMT/x86_64-unix/winemetal.so" "$W/x86_64-unix/"
for d in winemetal d3d11 dxgi d3d10core; do
  cp "$DXMT/x86_64-windows/$d.dll" "$W/x86_64-windows/"
  cp "$DXMT/i386-windows/$d.dll" "$W/i386-windows/" 2>/dev/null || true
done
mkdir -p "$OUT/share/cinecore/dxmt"
cp "$DXMT/x86_64-windows/winemetal.dll" "$OUT/share/cinecore/dxmt/winemetal64.dll"
cp "$DXMT/i386-windows/winemetal.dll" "$OUT/share/cinecore/dxmt/winemetal32.dll" 2>/dev/null || true
echo "engine=wine-staging 11.21 (Gcenx) + winemac.so con macdrv_functions + wined3d/d3d9 Cinecore (patch mac/wine-patches/0003); dxmt=v0.80" > "$OUT/share/cinecore/runtime-version"
echo "cinecore-download-handshake=1" >> "$OUT/share/cinecore/runtime-version"
echo "cinecore-fullscreen-space=1" >> "$OUT/share/cinecore/runtime-version"
echo "cinecore-video-mouse-passthrough=1" >> "$OUT/share/cinecore/runtime-version"
echo "cinecore-video-stacking=1" >> "$OUT/share/cinecore/runtime-version"
echo "cinecore-native-fullscreen-level=1" >> "$OUT/share/cinecore/runtime-version"
echo "==> fatto: $(du -sh "$OUT" | cut -f1)"
nm -gU "$OUT/lib/wine/x86_64-unix/winemac.so" | grep macdrv_functions
