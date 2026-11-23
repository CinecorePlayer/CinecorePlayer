#!/usr/bin/env bash
# Prepara il pacchetto di una release per Mac da dist/CinecorePlayer2026.app (da creare prima con mac/build-native-app.sh).
# Il canale degli aggiornamenti dell'app (MacUpdate.DefaultFeed) legge le release GitHub di CinecorePlayer/CinecorePlayer:
# vale una release con tag "beta-v<versione>-Avalonia-MacOs" e un allegato .zip con "Mac" nel nome. GitHub pubblica da solo l'impronta
# SHA-256 dell'allegato ("digest"), che l'app controlla prima di installare.
# Lo script NON pubblica nulla: crea lo zip in dist/releases e stampa il comando gh da lanciare a mano.
# Uso: bash mac/make-mac-release.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/dist/CinecorePlayer2026.app"
[[ -d "$APP" ]] || { echo "Manca $APP: lancia prima mac/build-native-app.sh" >&2; exit 1; }
VERSION="$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")"
ID="$(plutil -extract CFBundleIdentifier raw "$APP/Contents/Info.plist")"
[[ "$ID" == "com.cinecore.player.native" ]] || { echo "Identificativo inatteso: $ID" >&2; exit 1; }
OUT="$ROOT/dist/releases"
ZIP="$OUT/CinecorePlayer-Mac-$VERSION-arm64.zip"
mkdir -p "$OUT"
rm -f "$ZIP"
# ditto conserva firma, attributi e collegamenti simbolici dei framework (zip normale no).
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
SHA="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
echo "Pacchetto: $ZIP"
echo "SHA-256:   $SHA"
echo "Dimensione: $(stat -f %z "$ZIP") byte"
echo

# Immagine disco da distribuire: finestra con sfondo (mac/dmg/make-background.swift), l'app e il collegamento ad
# Applicazioni con la freccia in mezzo, icona del volume di Cinecore (dmgbuild, mac/dmg/dmg-settings.py: scrive la
# disposizione della finestra senza aprire il Finder). I componenti di terze parti NON sono dentro: al primo avvio l'app
# scarica il motore di Cinecore e madVR, MPC Video Renderer, LAV Filters e XySubFilter dai siti dei loro autori.
DMG="$OUT/CinecorePlayer2026-Mac-$VERSION-arm64.dmg"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/cinecore-dmg.XXXXXX")"
trap 'rm -rf -- "$WORK"' EXIT
swift "$ROOT/mac/dmg/make-background.swift" "$ROOT/Assets/logo.png" "$WORK/bg.png" 1
swift "$ROOT/mac/dmg/make-background.swift" "$ROOT/Assets/logo.png" "$WORK/bg@2x.png" 2
tiffutil -cathidpicheck "$WORK/bg.png" "$WORK/bg@2x.png" -out "$WORK/background.tiff" >/dev/null
# dmgbuild in un ambiente Python suo (una volta sola, in dist/tools: non entra nel progetto).
VENV="$ROOT/dist/tools/dmgbuild-venv"
if [[ ! -x "$VENV/bin/dmgbuild" ]]; then python3 -m venv "$VENV" && "$VENV/bin/pip" install -q "dmgbuild==2.0.0"; fi
rm -f "$DMG"
"$VENV/bin/dmgbuild" -s "$ROOT/mac/dmg/dmg-settings.py" -D app="$APP" -D icon="$APP/Contents/Resources/Cinecore.icns" \
    -D background="$WORK/background.tiff" "Cinecore Player" "$DMG" >/dev/null
DMG_SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
echo "Immagine disco: $DMG"
echo "SHA-256:   $DMG_SHA"
echo "Dimensione: $(stat -f %z "$DMG") byte"
echo
TAG="beta-v$VERSION-Avalonia-MacOs"
echo "Per pubblicare (tag $TAG, release separata da quelle di Windows):"
echo "  release gia' creata su GitHub:  gh release upload $TAG \"$DMG\" \"$ZIP\" -R CinecorePlayer/CinecorePlayer"
# Note della versione: mac/RELEASE_NOTES_macOS_<versione>.md se c'e', altrimenti quelle della Beta 1.
NOTES="$ROOT/mac/RELEASE_NOTES_macOS_$VERSION.md"; [[ -f "$NOTES" ]] || NOTES="$ROOT/mac/RELEASE_NOTES_macOS_Beta1.md"
# Impronte SHA-256 in fondo alle note della versione (rifatte a ogni pacchetto: cambiano a ogni build).
if [[ "$NOTES" == *"_$VERSION.md" ]]; then
  python3 -I - "$NOTES" "$(basename "$DMG")" "$DMG_SHA" "$(basename "$ZIP")" "$SHA" <<'PY'
import sys, re
notes, dmg, dmg_sha, zip_, zip_sha = sys.argv[1:6]
text = open(notes, encoding='utf-8').read()
text = re.sub(r'\n## SHA-256\n.*\Z', '', text, flags=re.S).rstrip('\n') + '\n'
text += f"\n## SHA-256\n\n| File | SHA-256 |\n|---|---|\n| `{dmg}` | `{dmg_sha}` |\n| `{zip_}` | `{zip_sha}` |\n"
open(notes, 'w', encoding='utf-8').write(text)
PY
  echo "SHA-256 scritti in $NOTES"
fi
# Accanto ai pacchetti: le impronte in file .sha256 e una copia delle note, tutto nella stessa cartella.
(cd "$OUT" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256" && shasum -a 256 "$(basename "$ZIP")" > "$(basename "$ZIP").sha256")
cp "$NOTES" "$OUT/"
LABEL="$(sed -n 's|.*<InformationalVersion>\(.*\)</InformationalVersion>.*|\1|p' "$ROOT/mac-native/CinecoreMac/CinecoreMac.csproj" | head -1)"
echo "  altrimenti:                     gh release create $TAG \"$DMG\" \"$ZIP\" -R CinecorePlayer/CinecorePlayer --prerelease --title \"Cinecore Player ${LABEL:-Beta} for macOS ($VERSION)\" --notes-file \"$NOTES\""
