#!/usr/bin/env bash
# Prepara (o aggiorna) il prefisso Wine di Cinecore Player su macOS:
# Visual C++, d3dcompiler, caratteri, DXMT, decoder LAV/MPC Audio, registrazione di madVR, MPC-VR,
# XySubFilter, Retina e DPI. Lo avvia il lanciatore del bundle al primo avvio; da terminale e' uguale.
#
# Uso:  bash setup-prefix.sh [--dpi N] [--no-retina] [--force] [--progress]
#   --dpi N       DPI di Windows dentro Wine (96 = 100%, 192 = 200%). Predefinito: 192 su schermi Retina.
#   --no-retina   Wine lavora in punti (immagine scalata da macOS) invece che nei pixel reali dello schermo
#   --force       rifa' tutti i passi anche se il prefisso e' gia' pronto
#   --progress    righe "@@step N/T testo" per il lanciatore grafico
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/cinecore-env.sh"

DPI=""; RETINA=1; FORCE=0; PROGRESS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dpi) DPI="$2"; shift 2;;
    --no-retina) RETINA=0; shift;;
    --force) FORCE=1; shift;;
    --progress) PROGRESS=1; shift;;
    -h|--help) sed -n 2,11p "$0"; exit 0;;
    *) echo "Opzione sconosciuta: $1" >&2; exit 2;;
  esac
done

TOTAL_STEPS=7; STEP=0
say()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
step() { STEP=$((STEP+1)); if [[ $PROGRESS -eq 1 ]]; then echo "@@step $STEP/$TOTAL_STEPS $*"; else say "$*"; fi; }
warn() { if [[ $PROGRESS -eq 1 ]]; then echo "@@warn $*"; else printf '\033[33m!!\033[0m %s\n' "$*" >&2; fi; }
die()  { if [[ $PROGRESS -eq 1 ]]; then echo "@@error $*"; else printf '\033[31mERRORE:\033[0m %s\n' "$*" >&2; fi; exit 1; }
LOG="$CINECORE_DATA/setup.log"
mkdir -p "$CINECORE_DATA"; : > "$LOG"
quiet() { "$@" >>"$LOG" 2>&1; }

[[ -x "$WINE" ]] || die "Runtime Wine non trovato (CINECORE_RUNTIME=$CINECORE_RUNTIME)."
[[ -f "$CINECORE_APP/CinecorePlayer2025.exe" ]] || die "Cinecore Player non trovato (CINECORE_APP=$CINECORE_APP)."
arch -x86_64 /usr/bin/true 2>/dev/null || die "Serve Rosetta 2: esegui  softwareupdate --install-rosetta --agree-to-license"

STAMP="$WINEPREFIX/.cinecore-setup"
WANT="$CINECORE_SETUP_VERSION"
DONE="$(cat "$STAMP" 2>/dev/null || true)"
[[ $FORCE -eq 1 ]] && DONE=""

# ---------------------------------------------------------------- 1. prefisso
step "Preparo l'ambiente Windows (Wine)"
mkdir -p "$(dirname "$WINEPREFIX")"
if [[ ! -f "$WINEPREFIX/system.reg" ]]; then
  quiet "$WINE" wineboot -i || true
  quiet "$WINESERVER" -w || true
fi
[[ -f "$WINEPREFIX/system.reg" ]] || die "Wine non ha creato il prefisso (vedi $LOG)."
quiet "$WINE" reg add 'HKCU\Software\Wine' /v Version /d win10 /f || true

# ---------------------------------------------------------------- 2. componenti Windows
step "Installo Visual C++, d3dcompiler e i caratteri"
if [[ "$DONE" != "$WANT" ]]; then
  export WINETRICKS_LATEST_VERSION_CHECK=disabled
  WT=(winetricks -q --unattended)
  if command -v winetricks >/dev/null || [[ -x "$HERE/winetricks" ]]; then
    command -v winetricks >/dev/null || WT=("$HERE/winetricks" -q --unattended)
    quiet "${WT[@]}" vcrun2022 || warn "Visual C++ 2015-2022 non installato."
    quiet "${WT[@]}" d3dcompiler_47 || warn "d3dcompiler_47 non installato."
    quiet "${WT[@]}" corefonts || warn "Caratteri di base non installati."
  else
    warn "winetricks non trovato: salto Visual C++ e caratteri."
  fi
fi

# ---------------------------------------------------------------- 3. DXMT
step "Attivo DXMT (Direct3D 11 su Metal)"
SYS32="$WINEPREFIX/drive_c/windows/system32"; SYSWOW="$WINEPREFIX/drive_c/windows/syswow64"
DX="$WINE_ROOT/share/cinecore/dxmt"
[[ -f "$DX/winemetal64.dll" ]] && cp -f "$DX/winemetal64.dll" "$SYS32/winemetal.dll"
[[ -f "$DX/winemetal32.dll" && -d "$SYSWOW" ]] && cp -f "$DX/winemetal32.dll" "$SYSWOW/winemetal.dll"
# Le DLL di DXMT sono "builtin": nessun override native deve coprirle.
for d in d3d11 dxgi d3d10core; do quiet "$WINE" reg delete 'HKCU\Software\Wine\DllOverrides' /v "$d" /f || true; done

# ---------------------------------------------------------------- 4. decoder
TP="$CINECORE_APP/third-parties"
winpath() { "$WINE" winepath -w "$1" 2>/dev/null | tr -d '\r'; }
step "Installo i decoder (LAV, MPC Audio)"
# Gli installer Inno Setup a 32 bit vanno in crash nella Wine di macOS (solo WoW64): si copiano i file
# estratti in anticipo da build-runtime.sh (o con innoextract qui) e si registrano i filtri x64.
PF="$WINEPREFIX/drive_c/Program Files"
DEC="$WINE_ROOT/share/cinecore/decoders"
dec_dir() {   # $1 = nome installer senza .exe -> cartella con app/
  if [[ -d "$DEC/$1/app" ]]; then echo "$DEC/$1"; return 0; fi
  command -v innoextract >/dev/null && [[ -f "$TP/installers/$1.exe" ]] || return 1
  local t; t="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-dec.XXXXXX")"
  innoextract -s -d "$t" "$TP/installers/$1.exe" >>"$LOG" 2>&1 && echo "$t"
}
put_filters() {   # $1 installer, $2 sottocartella sorgente in app/, $3 destinazione in Program Files, $4.. filtri
  local src dst f; src="$(dec_dir "$1")" || { warn "$1: file non disponibili (serve innoextract)."; return 0; }
  dst="$PF/$3"; mkdir -p "$dst"; cp -Rf "$src/app/$2/." "$dst/"
  shift 3
  for f in "$@"; do quiet "$WINE" regsvr32 /s "$(winpath "$dst/$f")" || warn "Registrazione di $f non riuscita."; done
}
if [[ "$DONE" != "$WANT" ]]; then
  put_filters LAVFilters-0.80 x64 "LAV Filters/x64" LAVSplitter.ax LAVVideo.ax LAVAudio.ax
  put_filters MPC-Audio-Decoder-x64 . "MPC Audio Decoder" MpaDecFilter.ax
fi

# ---------------------------------------------------------------- 5. renderer
step "Registro madVR, MPC Video Renderer e XySubFilter"
# Il player carica madVR e MPC-VR dal file .ax; la registrazione serve alle loro pagine delle impostazioni.
# Il percorso cambia se il bundle viene spostato: si registra a ogni aggiornamento del prefisso.
reg_ax() { [[ -f "$1" ]] && { quiet "$WINE" regsvr32 /s "$(winpath "$1")" || warn "Registrazione di $(basename "$1") non riuscita."; }; return 0; }
reg_ax "$TP/madVR09217/madVR64.ax"
for d in "$TP"/MpcVideoRenderer-*; do reg_ax "$d/MpcVideoRenderer64.ax"; done
for d in "$TP"/XySubFilter_*_x64; do reg_ax "$d/XySubFilter.dll"; done
# MPC Video Renderer su DXMT (Direct3D 11 -> Metal): D3D11 si', processore video D3D11 no (DXMT non ha
# ID3D11VideoDevice: la conversione la fanno gli shader), swap effect "discard" (flip sequential non supportato).
MPCVR_KEY='HKCU\Software\MPC-BE Filters\MPC Video Renderer'
for v in "UseD3D11 1" "VPEnableNV12 0" "VPEnableP01x 0" "VPEnableYUY2 0" "VPEnableOther 0" "SwapEffect 0" "ShowStatistics 0"; do
  set -- $v; quiet "$WINE" reg add "$MPCVR_KEY" /v "$1" /t REG_DWORD /d "$2" /f || true
done

# ---------------------------------------------------------------- 6. schermo
step "Adatto la scala dello schermo (Retina)"
MAC='HKCU\Software\Wine\Mac Driver'
if [[ $RETINA -eq 1 ]]; then
  # Pixel reali dello schermo: madVR e mpv scalano il video su tutti i pixel, il testo resta nitido.
  quiet "$WINE" reg add "$MAC" /v RetinaMode /d y /f || true
  if [[ -z "$DPI" ]]; then
    if system_profiler SPDisplaysDataType 2>/dev/null | grep -q "Retina"; then DPI=192; else DPI=96; fi
  fi
else
  quiet "$WINE" reg add "$MAC" /v RetinaMode /d n /f || true
  DPI="${DPI:-96}"
fi
# Cmd come Ctrl (Cmd+C, Cmd+V...), Opzione come Alt; finestre a tutto schermo nello Space di macOS.
quiet "$WINE" reg add "$MAC" /v LeftCommandIsCtrl /d y /f || true
quiet "$WINE" reg add "$MAC" /v RightCommandIsCtrl /d y /f || true
quiet "$WINE" reg add "$MAC" /v LeftOptionIsAlt /d y /f || true
quiet "$WINE" reg add "$MAC" /v RightOptionIsAlt /d y /f || true
quiet "$WINE" reg add 'HKCU\Control Panel\Desktop' /v LogPixels /t REG_DWORD /d "$DPI" /f || true

# ---------------------------------------------------------------- 7. fine
step "Completo la configurazione"
quiet "$WINESERVER" -w || true
echo "$WANT" > "$STAMP"
echo "$CINECORE_APP" > "$WINEPREFIX/.cinecore-app"
if [[ $PROGRESS -eq 1 ]]; then echo "@@done"; else say "Prefisso pronto in $WINEPREFIX (log: $LOG)"; fi
