#!/usr/bin/env bash
# libavcodec con i canali alti del DTS:X (fase 2 dell'audio spaziale): la stessa versione e la stessa configurazione
# dell'FFmpeg di Homebrew, piu' due patch del decoder dca:
#   mac/ffmpeg-patches/dca-xll-x-heights.patch         (FFmpegBuild, LGPL-2.1+: opzione x_heights -> 7.1.4)
#   mac/ffmpeg-patches/dca-xll-x-heights-unfold.patch  (Cinecore: il letto esce senza i canali alti, niente doppioni)
# Sostituisce solo libavcodec nel pacchetto (bundle-native-libs.py, CINECORE_AVCODEC_DYLIB): simboli e dipendenze
# devono coincidere con quelli di Homebrew, lo script lo verifica. Uso: bash mac/build-ffmpeg-dtsx.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BREW="${HOMEBREW_PREFIX:-/opt/homebrew}"
OUT="$ROOT/dist/mac-native/ffmpeg-dtsx"
BREW_AVCODEC="$(ls "$BREW"/opt/ffmpeg/lib/libavcodec.[0-9]*.dylib | grep -E 'libavcodec\.[0-9]+\.dylib$' | head -1)"
VERSION="$("$BREW/bin/ffmpeg" -hide_banner -version | head -1 | awk '{print $3}')"
WORK="$(mktemp -d /tmp/cinecore-ffmpeg.XXXXXX)"
trap 'rm -rf -- "$WORK"' EXIT

curl --fail --location --silent --show-error "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz" -o "$WORK/ffmpeg.tar.xz"
tar -xJf "$WORK/ffmpeg.tar.xz" -C "$WORK"
SRC="$WORK/ffmpeg-$VERSION"
patch -d "$SRC" -p1 < "$ROOT/mac/ffmpeg-patches/dca-xll-x-heights.patch"
patch -d "$SRC" -p1 < "$ROOT/mac/ffmpeg-patches/dca-xll-x-heights-unfold.patch"

export PATH="$BREW/bin:$PATH" PKG_CONFIG_PATH="$BREW/lib/pkgconfig:$BREW/opt/openssl/lib/pkgconfig"
# Le opzioni di Homebrew (ffmpeg -buildconf), con spenti i componenti che qui verrebbero rilevati da soli e che la
# libavutil di Homebrew non ha (Vulkan, X11, SDL).
(cd "$SRC" && ./configure --prefix="$WORK/inst" --enable-shared --disable-static --enable-pthreads --enable-version3 --cc=clang \
    --enable-gpl --enable-libsvtav1 --enable-libopus --enable-libx264 --enable-libmp3lame --enable-libdav1d --enable-libvmaf \
    --enable-libvpx --enable-libx265 --enable-openssl --enable-videotoolbox --enable-audiotoolbox --enable-neon \
    --disable-programs --disable-doc --disable-vulkan --disable-sdl2 --disable-libxcb --disable-xlib \
    --extra-cflags="-I$BREW/include" --extra-ldflags="-L$BREW/lib" >/dev/null)
make -C "$SRC" -j"$(sysctl -n hw.ncpu)" >/dev/null
make -C "$SRC" install >/dev/null

LIB="$WORK/inst/lib/$(basename "$BREW_AVCODEC")"
# Stessi simboli esportati e importati (a parte la libc) e stesse librerie di Homebrew, o niente.
diff <(nm -gU "$BREW_AVCODEC" | awk '{print $3}' | grep -v indirect | sort) <(nm -gU "$LIB" | awk '{print $3}' | grep -v indirect | sort)
diff <(nm -u "$BREW_AVCODEC" | grep '^_av' | sort) <(nm -u "$LIB" | grep '^_av' | sort)
# Riferimenti riscritti ai percorsi di Homebrew: bundle-native-libs.py li tratta come quelli della libreria originale.
for ref in $(otool -L "$LIB" | tail -n +2 | awk '{print $1}'); do
    name="$(basename "$ref")"
    brew_ref="$(otool -L "$BREW_AVCODEC" | tail -n +2 | awk '{print $1}' | grep "/$name\$" || true)"
    [[ -z "$brew_ref" ]] && { echo "dipendenza in piu' rispetto a Homebrew: $ref" >&2; exit 1; }
    [[ "$ref" != "$brew_ref" ]] && install_name_tool -change "$ref" "$brew_ref" "$LIB"
done
install_name_tool -id "$(otool -D "$BREW_AVCODEC" | tail -1)" "$LIB"
codesign --force --sign - "$LIB" >/dev/null 2>&1
mkdir -p "$OUT"
install -m 755 "$LIB" "$OUT/$(basename "$BREW_AVCODEC")"
echo "libavcodec DTS:X: $OUT/$(basename "$BREW_AVCODEC") (FFmpeg $VERSION)"
