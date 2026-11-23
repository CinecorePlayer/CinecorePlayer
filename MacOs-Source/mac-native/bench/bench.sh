#!/usr/bin/env bash
# Uso: bash mac-native/bench/bench.sh [secondi] [file...]
# BENCH_RENDERERS="madvr mpcvr", BENCH_SIZES="full window", BENCH_OUT=cartella dei risultati.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

export CINECORE_RUNTIME="${CINECORE_RUNTIME:-$ROOT/dist/mac/runtime-wine11}"
export BENCH_HOST="${BENCH_HOST:-$ROOT/dist/mac-native/videohost-current/VideoHost.exe}"
export BENCH_OUT="${BENCH_OUT:-$ROOT/dist/bench/$(date +%Y%m%d-%H%M%S)}"
export BENCH_PREFIX_SEED="${BENCH_PREFIX_SEED:-$HOME/Library/Application Support/Cinecore/wineprefix-w11}"
[[ -x "$CINECORE_RUNTIME/wine/bin/wine" && -f "$BENCH_HOST" ]] || {
  echo "Manca Wine 11 o VideoHost.exe. Vedi README.md." >&2; exit 1;
}
[[ -f "$BENCH_PREFIX_SEED/system.reg" ]] || {
  echo "Manca un prefisso Wine 11 preparato da usare come base. Vedi README.md." >&2; exit 1;
}

# Il benchmark copia il prefisso e usa una cache separata, anche se il player è aperto.
QA="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-bench.XXXXXX")"
trap 'rm -rf -- "$QA"' EXIT
ditto "$BENCH_PREFIX_SEED" "$QA/prefix"
export CINECORE_PREFIX="$QA/prefix" DXMT_SHADER_CACHE_PATH="$QA/dxmt"
export WINEDEBUG=-all DXMT_LOG_LEVEL=none
source "$ROOT/mac/cinecore-env.sh"
trap '"$WINESERVER" -k 2>/dev/null || true; "$WINESERVER" -w 2>/dev/null || true; rm -rf -- "$QA"' EXIT

# madVR salva settings.bin accanto al filtro, fuori dal registro Wine.
# Registra una copia privata per proteggere anche le impostazioni della build originale.
# Un crash lascia il backtrace nel .stderr della prova (debugger automatico di Wine).
"$WINE" reg add 'HKLM\Software\Microsoft\Windows NT\CurrentVersion\AeDebug' /v Auto /d 1 /f >/dev/null 2>&1 || true
"$WINE" reg add 'HKLM\Software\Microsoft\Windows NT\CurrentVersion\AeDebug' /v Debugger /d 'winedbg --auto %ld %ld' /f >/dev/null 2>&1 || true
MADVR="$CINECORE_APP/third-parties/madVR09217"
if [[ " ${BENCH_RENDERERS:-madvr mpcvr} " == *" madvr "* ]]; then
  ditto "$MADVR" "$QA/madVR"
  madvr_filter="$QA/madVR/madVR64.ax"
  "$WINE" regsvr32 /s "Z:${madvr_filter//\//\\}" >"$QA/registrazione-madvr.log" 2>&1 || {
    tail -12 "$QA/registrazione-madvr.log" >&2; exit 1;
  }
fi
python3 "$ROOT/mac-native/bench/bench.py" "$@"
