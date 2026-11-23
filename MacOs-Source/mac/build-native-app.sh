#!/usr/bin/env bash
# Crea dist/CinecorePlayer2026.app (nome dell'utente; il 20/11 corretto da "CinecorePlayere2026", eseguibile CinecorePlayer2026): interfaccia nativa (Avalonia, Apple Silicon), video con libmpv nel pacchetto
# (Contents/Frameworks) e, per madVR/MPC-VR, il VideoHost sotto Wine (dist/mac/runtime-wine11, dist/mac-native/videohost-current). Uso: bash mac/build-native-app.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/dist/CinecorePlayer2026.app"
STAGE="$ROOT/dist/.CinecorePlayer2026-build.app"
OLD_APP="$ROOT/dist/CinecorePlayere2026.app"
PUB="$ROOT/dist/mac-native/cinecore-mac-publish"
HOST_PUB="$ROOT/dist/mac-native/cinecore-videohost-publish"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
"$DOTNET" publish "$ROOT/mac-native/CinecoreMac/CinecoreMac.csproj" -c Release -r osx-arm64 --self-contained -o "$PUB" >/dev/null
"$DOTNET" publish "$ROOT/mac-native/VideoHost/VideoHost.csproj" -c Release -r win-x64 --self-contained -o "$HOST_PUB" >/dev/null
[[ ! -e "$STAGE" ]] || { echo "Esiste già un pacchetto di build: $STAGE" >&2; exit 1; }
mkdir -p "$STAGE/Contents/MacOS" "$STAGE/Contents/Resources"
trap 'rm -rf -- "$STAGE"' EXIT
ditto "$PUB" "$STAGE/Contents/MacOS"
# L'eseguibile porta il nome dell'app (l'apphost di .NET trova CinecoreMac.dll anche rinominato).
mv "$STAGE/Contents/MacOS/CinecoreMac" "$STAGE/Contents/MacOS/CinecorePlayer2026"
# Motore madVR/MPC-VR: i pezzi nostri (mac/build-engine-overlay.sh) e il manifesto con gli indirizzi ufficiali e le
# impronte di Wine e dei filtri, che l'app scarica dai siti degli autori al primo avvio.
[[ -f "$ROOT/dist/engine-pack/overlay/cinecore-settings.reg" ]] || bash "$ROOT/mac/build-engine-overlay.sh"
ditto "$ROOT/dist/engine-pack/overlay" "$STAGE/Contents/Resources/Engine"
# Una cartella che finisce in .app dentro il pacchetto sembrerebbe un'app annidata: nell'app si chiama wine-overlay.
mv "$STAGE/Contents/Resources/Engine/MetalSharp-Wine-IOSurface.app" "$STAGE/Contents/Resources/Engine/wine-overlay"
cp "$ROOT/mac/engine-manifest.json" "$STAGE/Contents/Resources/Engine/engine-manifest.json"
# Il VideoHost va aggiornato insieme al player. Wine e i filtri restano nel motore scaricabile.
ditto "$HOST_PUB" "$STAGE/Contents/Resources/VideoHost"
# Il rilevatore gira in un processo separato con FFmpeg e ffprobe già inclusi nell'app.
"$DOTNET" publish "$ROOT/IntroOutroDetector/IntroOutroDetector.csproj" -c Release -r osx-arm64 \
    -p:CinecoreMacBundle=true --self-contained -o "$STAGE/Contents/MacOS/IntroOutroDetector" >/dev/null
xcrun clang -arch arm64 -mmacosx-version-min=13.0 -fobjc-arc -dynamiclib -framework AppKit -framework QuartzCore -framework WebKit -framework MediaPlayer \
    -Wl,-install_name,@rpath/libCinecoreGlass.dylib "$ROOT/mac/MacLiquidGlass.m" "$ROOT/mac/MacWebSession.m" "$ROOT/mac/MacNowPlaying.m" -o "$STAGE/Contents/MacOS/libCinecoreGlass.dylib"
mv "$STAGE/Contents/MacOS/Assets" "$STAGE/Contents/Resources/Assets"
# Script del riconoscimento dei testi (lo stesso di Windows): lo trova FasterWhisperLyricsRecognitionBackend accanto all'app.
mkdir -p "$STAGE/Contents/MacOS/LyricsSynchronization"
cp "$ROOT/LyricsSynchronization/music_lyrics_backend.py" "$STAGE/Contents/MacOS/LyricsSynchronization/"
# libmpv (+ librerie, MoltenVK) e ffmpeg/ffprobe dentro il pacchetto: l'app non dipende da Homebrew.
BREW="${HOMEBREW_PREFIX:-/opt/homebrew}"
MPV_PATCH="$ROOT/mac/mpv-patches/coreaudio-atmos-v0.42.0.patch"
MPV_LIB="${CINECORE_MPV_DYLIB:-$ROOT/dist/mac-native/mpv-coreaudio/libmpv.2.dylib}"
if [[ ! -f "$MPV_LIB" || "$MPV_PATCH" -nt "$MPV_LIB" ]]; then
    bash "$ROOT/mac/build-mpv-coreaudio.sh" "$MPV_LIB"
fi
xcrun clang -arch arm64 -mmacosx-version-min=15.0 -std=c11 -O2 -Wall -Wextra -Werror \
    -I"$BREW/opt/ffmpeg/include" -L"$BREW/opt/ffmpeg/lib" -framework CoreVideo \
    "$ROOT/mac-native/VideoToolboxBridge/vt_native.c" -lavcodec -lavutil \
    -o "$STAGE/Contents/MacOS/cinecore-vt-decode"
# Lo stesso decoder come libreria nativa caricata DENTRO il processo di Wine del VideoHost (CinecoreVideoToolbox.dll la
# apre con le chiamate native di Wine): niente programma separato ne' socket. Vedi VideoToolboxSession.
xcrun clang -arch arm64 -mmacosx-version-min=15.0 -std=c11 -O2 -Wall -Wextra -Werror -shared -fvisibility=hidden \
    -I"$BREW/opt/ffmpeg/include" -L"$BREW/opt/ffmpeg/lib" -framework CoreVideo \
    "$ROOT/mac-native/VideoToolboxBridge/vt_unix.c" -lavcodec -lavutil \
    -o "$STAGE/Contents/MacOS/cinecorevt.so"
# libavcodec con i canali alti del DTS:X, se e' stata compilata (bash mac/build-ffmpeg-dtsx.sh).
AVCODEC_DTSX="$(ls "$ROOT"/dist/mac-native/ffmpeg-dtsx/libavcodec.*.dylib 2>/dev/null | head -1 || true)"
CINECORE_AVCODEC_DYLIB="$AVCODEC_DTSX" CINECORE_MPV_DYLIB="$MPV_LIB" python3 "$ROOT/mac/bundle-native-libs.py" "$STAGE" "$STAGE/Contents/MacOS/cinecore-vt-decode" "$STAGE/Contents/MacOS/cinecorevt.so"
# La libreria vive nel processo di Wine (@executable_path = wine): i riferimenti a FFmpeg partono dalla sua cartella.
for ref in $(otool -L "$STAGE/Contents/MacOS/cinecorevt.so" | awk '/@executable_path\/..\/Frameworks\//{print $1}'); do
    install_name_tool -change "$ref" "${ref/@executable_path/@loader_path}" "$STAGE/Contents/MacOS/cinecorevt.so"
done
codesign --force --sign - "$STAGE/Contents/MacOS/cinecorevt.so"
# Licenze di LibTrueHD (collegata dentro libmpv per il TrueHD Atmos spaziale): MIT, Apache-2.0 e NOTICE delle dipendenze.
if [[ -d "$(dirname "$MPV_LIB")/LibTrueHD" ]]; then
    mkdir -p "$STAGE/Contents/Resources/Licenses"
    ditto "$(dirname "$MPV_LIB")/LibTrueHD" "$STAGE/Contents/Resources/Licenses/LibTrueHD"
fi
# Versioni dei componenti copiati nel pacchetto (le mostra Strumenti › Componenti esterni).
"$BREW/bin/brew" list --versions ffmpeg libplacebo molten-vk rubberband > "$STAGE/Contents/Resources/components.txt" 2>/dev/null || true
echo "mpv 0.42.0 (CoreAudio Dolby)" >> "$STAGE/Contents/Resources/components.txt"
# Icona: il simbolo del logo (Assets/logo.png), lo stesso di cinecore.ico su Windows.
cp "$ROOT/mac/Cinecore.icns" "$STAGE/Contents/Resources/Cinecore.icns"
# Icona del telecomando sul telefono (RemoteServer /icon.png): la stessa, in PNG 512 px.
sips -s format png --resampleWidth 512 "$ROOT/mac/Cinecore.icns" --out "$STAGE/Contents/Resources/Assets/remote-icon.png" >/dev/null 2>&1 || true
VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$ROOT/mac-native/CinecoreMac/CinecoreMac.csproj" | head -1)
VERSION=${VERSION:-0.1}
cat > "$STAGE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Cinecore Player</string>
  <key>CFBundleDisplayName</key><string>Cinecore Player</string>
  <key>CFBundleIdentifier</key><string>com.cinecore.player.native</string>
  <key>CFBundleExecutable</key><string>CinecorePlayer2026</string>
  <key>CFBundleIconFile</key><string>Cinecore.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key><array><dict>
    <key>CFBundleTypeName</key><string>Video</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSItemContentTypes</key><array><string>public.movie</string><string>public.iso-image</string></array>
  </dict><dict>
    <key>CFBundleTypeName</key><string>Video (altri formati)</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>CFBundleTypeExtensions</key><array><string>mkv</string><string>webm</string><string>m2ts</string><string>mts</string><string>ts</string><string>avi</string><string>wmv</string><string>flv</string><string>iso</string><string>vob</string></array>
  </dict><dict>
    <key>CFBundleTypeName</key><string>Musica</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSHandlerRank</key><string>Alternate</string>
    <key>LSItemContentTypes</key><array><string>public.audio</string></array>
    <key>CFBundleTypeExtensions</key><array><string>flac</string><string>ogg</string><string>opus</string><string>dsf</string><string>ape</string></array>
  </dict><dict>
    <key>CFBundleTypeName</key><string>Playlist di Windows Media Player</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSHandlerRank</key><string>Alternate</string>
    <key>CFBundleTypeExtensions</key><array><string>wpl</string></array>
  </dict><dict>
    <key>CFBundleTypeName</key><string>Foto</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSHandlerRank</key><string>Alternate</string>
    <key>LSItemContentTypes</key><array><string>public.image</string></array>
  </dict></array>
</dict></plist>
PLIST
# Firma ad hoc (o CINECORE_CODESIGN_IDENTITY). Le credenziali non dipendono dalla firma: stanno nell'archivio
# dell'app (SecretVault, vault.json), non nel Portachiavi, quindi nessuna richiesta dopo un aggiornamento.
# Con un'identita' stabile ("Cinecore Local Signing" nel Portachiavi) macOS ricorda i permessi (es. Volumi rimovibili)
# fra una build e l'altra; con la firma ad hoc ogni build e' un'app nuova e li richiede.
DEFAULT_IDENTITY="-"
security find-identity -p codesigning 2>/dev/null | grep -q '"Cinecore Local Signing"' && DEFAULT_IDENTITY="Cinecore Local Signing"
SIGN_IDENTITY="${CINECORE_CODESIGN_IDENTITY:-$DEFAULT_IDENTITY}"
# Firma ad hoc con requisito sull'identificativo: macOS (TCC) lega i permessi (Volumi rimovibili, ecc.) al requisito
# designato; con quello predefinito (l'impronta del programma) ogni build era un'app nuova e li richiedeva tutti.
if [[ "$SIGN_IDENTITY" == "-" ]]; then
  codesign --force --deep --sign - --identifier com.cinecore.player.native \
    -r='designated => identifier "com.cinecore.player.native"' "$STAGE"
else
  codesign --force --deep --sign "$SIGN_IDENTITY" --identifier com.cinecore.player.native "$STAGE"
fi
codesign --verify --deep --strict "$STAGE"
if [[ -e "$APP" ]]; then
  BACKUP="$ROOT/dist/backups/native-app-$(date +%Y%m%d-%H%M%S)-$$.app"
  mkdir -p "$ROOT/dist/backups"
  mv "$APP" "$BACKUP"
fi
mv "$STAGE" "$APP"
# Il pacchetto col nome vecchio va con i backup (altrimenti restano due app uguali).
if [[ -e "$OLD_APP" ]]; then mkdir -p "$ROOT/dist/backups"; mv "$OLD_APP" "$ROOT/dist/backups/native-app-oldname-$(date +%Y%m%d-%H%M%S).app"; fi
touch "$APP"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
if [[ -x "$LSREGISTER" ]]; then "$LSREGISTER" -f "$APP" || true; fi
echo "Creato: $APP"
