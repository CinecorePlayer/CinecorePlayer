#!/usr/bin/env bash
# Prova del runtime "WineCX 24.0.7" (Wine di CrossOver, build Sikarugir) + DXMT (Direct3D 11 -> Metal).
# Assembla dist/mac/runtime-cx24/wine e lo avvia in un prefisso temporaneo.
set -euo pipefail
R="$HOME/Library/Application Support/Cinecore/runtime"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
D="$ROOT/dist/mac/runtime-cx24/wine"
rm -rf "$D"; mkdir -p "$D"
cp -R "$R/cx24/wswine.bundle/bin" "$R/cx24/wswine.bundle/lib" "$R/cx24/wswine.bundle/share" "$D/"
find "$R/wine-src/Wine Staging.app/Contents/Resources/wine/lib" -maxdepth 1 -name '*.dylib' -exec cp -P {} "$D/lib/" \;
X="$R/dxmt/v0.80"
cp "$X/x86_64-unix/winemetal.so" "$D/lib/wine/x86_64-unix/"
for d in winemetal d3d11 dxgi d3d10core; do
  cp "$X/x86_64-windows/$d.dll" "$D/lib/wine/x86_64-windows/"
  cp "$X/i386-windows/$d.dll" "$D/lib/wine/i386-windows/" 2>/dev/null || true
done
mkdir -p "$D/share/cinecore/dxmt"
cp "$X/x86_64-windows/winemetal.dll" "$D/share/cinecore/dxmt/winemetal64.dll"
cp "$X/i386-windows/winemetal.dll" "$D/share/cinecore/dxmt/winemetal32.dll"
echo "engine=WineCX 24.0.7; dxmt=v0.80" > "$D/share/cinecore/runtime-version"
echo "Runtime: $(du -sh "$D" | cut -f1)"

export WINEPREFIX="${TMPDIR:-/tmp}/cinecore-cx24-test" DYLD_FALLBACK_LIBRARY_PATH="$D/lib:/usr/lib" WINEDEBUG=err+all
rm -rf "$WINEPREFIX"
"$D/bin/wine" --version
"$D/bin/wine" cmd /c ver 2>&1 | grep -v fixme | tail -3
echo "File in system32: $(ls "$WINEPREFIX/drive_c/windows/system32" | wc -l | tr -d ' ')  (più di 500 = prefisso creato bene)"
