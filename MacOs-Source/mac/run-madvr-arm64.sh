#!/usr/bin/env bash
# Avvio sperimentale di madVR senza Rosetta.
# Uso: bash mac/run-madvr-arm64.sh [--videotoolbox | --iosurface] [film]
# Richiede MetalSharp Wine ARM64 6.0 in dist/mac/runtime-arm64, oppure CINECORE_WINE_ARM64.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="$root/dist/mac/runtime-arm64"
bundle_default="$runtime/MetalSharp-Wine.app"
prefix_default="$runtime/prefix"
videotoolbox=0
case "${1:-}" in
    --videotoolbox) videotoolbox=1; shift ;;
    --iosurface)
        videotoolbox=1
        bundle_default="$runtime/MetalSharp-Wine-IOSurface.app"
        prefix_default="$runtime/prefix-iosurface"
        export GRAPHICS_BACKEND=mullion
        shift
        ;;
esac
bundle="${CINECORE_WINE_ARM64:-$bundle_default}"
prefix="${CINECORE_ARM64_PREFIX:-$prefix_default}"
app="$root/dist/Cinecore Player (nativo).app/Contents/MacOS/CinecoreMac"
third="$root/third-parties"

fail() { printf '%s\n' "$*" >&2; exit 1; }
[[ -d "$bundle" ]] || fail "Runtime ARM64 assente: $bundle"
bundle="$(cd "$bundle" && pwd -P)"
bin="$bundle/Contents/MacOS"
[[ -x "$bin/wine" && -x "$bin/wineserver" ]] || fail "Serve MetalSharp-Wine.app (ARM64) in $runtime."
for tool in wine wineserver; do
    [[ "$(/usr/bin/lipo -archs "$bin/$tool")" == arm64 ]] || fail "$tool deve essere ARM64."
done

mkdir -p "$prefix"
prefix="$(cd "$prefix" && pwd -P)"
owned="$prefix/.cinecore-arm64"
ready="$prefix/.cinecore-arm64-ready"
if [[ ! -f "$ready" ]]; then
    printf 'Preparo il prefisso ARM64. Log in %s/cinecore-arm64-setup.log\n' "$prefix"
    (
        lock="$prefix/.cinecore-arm64-lock"
        mkdir "$lock" || fail "Preparazione del prefisso ARM64 già in corso."
        trap 'rmdir "$lock"' EXIT
        [[ ! -f "$ready" ]] || exit 0
        exec >"$prefix/cinecore-arm64-setup.log" 2>&1
        [[ ! -e "$prefix/system.reg" || -f "$owned" ]] || fail "Il percorso contiene già un altro prefisso Wine."
        /usr/bin/codesign --verify --deep --strict "$bundle"
        touch "$owned"
        export WINEPREFIX="$prefix" WINEDEBUG=-all WINEMSYNC=1
        unset WINEARCH WINELOADER WINESERVER WINEDLLPATH WINEDLLOVERRIDES DYLD_FALLBACK_LIBRARY_PATH
        work="$(mktemp -d "$prefix/.setup.XXXXXX")"
        trap 'rm -rf -- "$work"; rmdir "$lock"' EXIT
        wine="$bin/wine"
        "$wine" wineboot -i
        "$bin/wineserver" -w

        filters="$prefix/drive_c/Cinecore"
        mkdir -p "$filters/LAV" "$filters/madVR" "$filters/XySubFilter" "$filters/MPCVR"
        "$bin/innoextract" --silent -d "$work" "$third/installers/LAVFilters-0.80.exe"
        cp -R "$work/app/x64/." "$filters/LAV/"
        if [[ ! -f "$filters/madVR/madVR64.ax" ]]; then
            cp -R "$third/madVR09217/." "$filters/madVR/"
        fi
        # madVR avvia da solo madHcCtrl.exe (icona e pannello, programma a 32 bit). Sotto FEX/WoW64 gira
        # a vuoto in RtlVirtualUnwind e occupa un core intero anche dopo la fine del film (misurato 11/11/2026).
        # Il rendering non ne ha bisogno: le impostazioni passano da mvrSettings64.dll.
        if [[ -f "$filters/madVR/madHcCtrl.exe" ]]; then
            mv -f "$filters/madVR/madHcCtrl.exe" "$filters/madVR/madHcCtrl.exe.off"
        fi
        cp "$third"/XySubFilter_*_x64/XySubFilter.dll "$filters/XySubFilter/"
        cp "$third"/MpcVideoRenderer-*/MpcVideoRenderer64.ax "$filters/MPCVR/"
        for filter in 'LAV\LAVSplitter.ax' 'LAV\LAVVideo.ax' 'LAV\LAVAudio.ax' \
            'madVR\madVR64.ax' 'XySubFilter\XySubFilter.dll' 'MPCVR\MpcVideoRenderer64.ax'; do
            "$wine" regsvr32 /s "C:\\Cinecore\\$filter"
        done
        "$wine" reg add 'HKCU\Software\Wine\Mac Driver' /v RetinaMode /d Y /f
        "$wine" reg add 'HKCU\Software\LAV\Video' /v NumThreads /t REG_DWORD /d 4 /f
        "$bin/wineserver" -w

        # Quartz ARM64X di MetalSharp 6.0 va in eccezione all'avvio del grafo DirectShow.
        # Copia il modulo PE x64 di Wine, che FEX traduce insieme ai filtri x64.
        # Il marcatore nel DOS stub impedirebbe a Wine di trattare la copia come DLL native.
        quartz="$root/dist/mac/runtime-wine11/wine/lib/wine/x86_64-windows/quartz.dll"
        python3 - "$quartz" "$work/quartz.dll" <<'PY'
from pathlib import Path
import sys

module = bytearray(Path(sys.argv[1]).read_bytes())
if module[64:80] != b"Wine builtin DLL":
    raise SystemExit("Formato del modulo Quartz Wine sconosciuto")
module[64] = ord("X")
Path(sys.argv[2]).write_bytes(module)
PY
        mv -f "$work/quartz.dll" "$prefix/drive_c/windows/system32/quartz.dll"
        touch "$ready"
    )
fi

[[ "${1:-}" != --prepare-only ]] || exit 0
[[ -x "$app" ]] || fail "Compila Cinecore con bash mac/build-native-app.sh."
if [[ "$videotoolbox" == 1 ]]; then
    [[ -f "$runtime/CinecoreVideoToolbox.dll" && -x "${app%/*}/cinecore-vt-decode" ]] ||
        fail "Compila il ponte con bash mac/build-videotoolbox-bridge.sh e ricompila l'app."
    export CINECORE_FEX_VIDEOTOOLBOX=1
fi
export CINECORE_WINE_ARM64="$bundle" CINECORE_ARM64_PREFIX="$prefix" CINECORE_RENDERER=madvr
exec "$app" "$@"
