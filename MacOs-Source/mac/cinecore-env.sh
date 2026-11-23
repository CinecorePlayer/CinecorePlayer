# Ambiente comune per Wine su macOS (da includere con "source").
# Variabili in ingresso facoltative: CINECORE_RUNTIME (cartella con wine/), CINECORE_PREFIX, CINECORE_APP.
# Dentro il bundle .app i percorsi si ricavano da Contents/Resources.

_cc_here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CINECORE_DATA="${CINECORE_DATA:-$HOME/Library/Application Support/Cinecore}"
if [[ -z "${CINECORE_RUNTIME:-}" ]]; then
  for _cc_c in "$_cc_here/.." "$_cc_here/../dist/mac/runtime-wine11" "$_cc_here/../dist/mac/runtime" "$CINECORE_DATA/runtime"; do
    [[ -x "$_cc_c/wine/bin/wine" ]] && { CINECORE_RUNTIME="$(cd "$_cc_c" && pwd)"; break; }
  done
fi
if [[ -z "${CINECORE_APP:-}" ]]; then
  for _cc_c in "$_cc_here/../app" "$_cc_here/../dist/mac/app"; do
    [[ -f "$_cc_c/CinecorePlayer2025.exe" ]] && { CINECORE_APP="$(cd "$_cc_c" && pwd)"; break; }
  done
fi
export CINECORE_RUNTIME CINECORE_APP CINECORE_DATA
WINE_ROOT="$CINECORE_RUNTIME/wine"
_cc_prefix="$CINECORE_DATA/wineprefix"
if [[ -f "$WINE_ROOT/share/cinecore/runtime-version" ]] &&
   grep -q '^engine=wine-staging 11\.21 (Gcenx) + winemac\.so con macdrv_functions; dxmt=v0\.80$' "$WINE_ROOT/share/cinecore/runtime-version"; then
  # Una versione distinta lascia utilizzabili i prefissi preparati con gli altri runtime.
  _cc_prefix="$CINECORE_DATA/wineprefix-11.21-dxmt"
fi
export WINEPREFIX="${CINECORE_PREFIX:-$_cc_prefix}"
CINECORE_SETUP_VERSION=4
export PATH="$WINE_ROOT/bin:$PATH"
export WINE="$WINE_ROOT/bin/wine" WINESERVER="$WINE_ROOT/bin/wineserver"
# Wine carica freetype, gnutls e MoltenVK con dlopen: devono stare nel percorso di ricerca.
export DYLD_FALLBACK_LIBRARY_PATH="$WINE_ROOT/lib:/usr/lib:/usr/local/lib"
export WINEARCH=win64
export WINEDEBUG="${WINEDEBUG:--all}"
# Sincronizzazione veloce fra thread (Mach semaphores): meno carico della CPU durante il video.
export WINEMSYNC="${WINEMSYNC:-1}"
# DXMT: cache degli shader fuori dal bundle, log solo su richiesta.
export DXMT_LOG_LEVEL="${DXMT_LOG_LEVEL:-none}"
export DXMT_SHADER_CACHE_PATH="${DXMT_SHADER_CACHE_PATH:-$HOME/Library/Caches/Cinecore/dxmt}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$DXMT_SHADER_CACHE_PATH" 2>/dev/null || true
unset _cc_here _cc_c _cc_prefix
