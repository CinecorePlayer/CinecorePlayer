#!/usr/bin/env bash
# Rebuild the libmpv shipped with the macOS player. Requires Meson, Ninja,
# pkg-config and the Homebrew libraries used by the mpv formula.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/dist/mac-native/mpv-coreaudio/libmpv.2.dylib}"
BREW="${HOMEBREW_PREFIX:-/opt/homebrew}"
WORK="$(mktemp -d /tmp/cinecore-mpv.XXXXXX)"
trap 'rm -rf -- "$WORK"' EXIT
for tool in meson ninja "${PKG_CONFIG:-pkg-config}"; do
    command -v "$tool" >/dev/null || { echo "Strumento di compilazione mancante: $tool" >&2; exit 1; }
done

curl --fail --location --silent --show-error \
    https://codeload.github.com/mpv-player/mpv/tar.gz/refs/tags/v0.42.0 -o "$WORK/mpv.tar.gz"
printf 'bcfebbfa2b65a6d88dc97ea18117831072373e38b5785a2afb874ed4df5070dd  %s\n' "$WORK/mpv.tar.gz" | shasum -a 256 -c
tar -xzf "$WORK/mpv.tar.gz" -C "$WORK"
export PKG_CONFIG_PATH="$BREW/lib/pkgconfig:$BREW/opt/libarchive/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}:$WORK/sdk-pc"
export MACOSX_DEPLOYMENT_TARGET=15.0
mkdir -p "$WORK/sdk-pc"
# FreeType's pkg-config metadata references libraries already present in the macOS SDK.
printf 'Name: zlib\nDescription: macOS SDK zlib\nVersion: 1.2.12\nLibs: -lz\n' > "$WORK/sdk-pc/zlib.pc"
printf 'Name: bzip2\nDescription: macOS SDK bzip2\nVersion: 1.0.8\nLibs: -lbz2\n' > "$WORK/sdk-pc/bzip2.pc"
patch -d "$WORK/mpv-0.42.0" -p1 < "$ROOT/mac/mpv-patches/coreaudio-atmos-v0.42.0.patch"
# Audio spaziale su cuffie e altoparlanti del Mac (AUSpatialMixer), solo con --coreaudio-spatial=auto|yes.
patch -d "$WORK/mpv-0.42.0" -p1 < "$ROOT/mac/mpv-patches/coreaudio-spatial-v0.42.0.patch"
# TrueHD Atmos con gli oggetti resi in 7.1.4 (--ad-truehd-atmos): LibTrueHD (MIT, crate truehd Apache-2.0) in Rust,
# a un commit fissato, collegata statica. Serve rustup (brew install rustup).
patch -d "$WORK/mpv-0.42.0" -p1 < "$ROOT/mac/mpv-patches/truehd-atmos-v0.42.0.patch"
# Serve la toolchain ufficiale (rustup, versione fissata in rust-toolchain.toml di LibTrueHD): il Rust di Homebrew
# ha la libreria standard in bitcode LLVM piu' nuovo del linker di Apple, e libmpv restava senza LibTrueHD.
export PATH="$BREW/opt/rustup/bin:$PATH"
command -v rustup >/dev/null || { echo "Strumento di compilazione mancante: rustup (brew install rustup)" >&2; exit 1; }
LIBTRUEHD_REV=12b26e487a2ade918e53380d47f5727883fedafc
git clone --quiet https://github.com/superuser404notfound/LibTrueHD.git "$WORK/LibTrueHD"
git -C "$WORK/LibTrueHD" checkout --quiet "$LIBTRUEHD_REV"
# Come il loro build.sh: solo staticlib, cosi' Rust fa l'LTO completo e scrive codice macchina (con cargo build e
# la rlib accanto gli oggetti restano bitcode LLVM, illeggibile per il linker di Apple).
(cd "$WORK/LibTrueHD" && cargo rustc --quiet --release --locked --lib --crate-type staticlib --target aarch64-apple-darwin)
THD_A="$WORK/LibTrueHD/target/aarch64-apple-darwin/release/libtruehd_atmos.a"
# Licenze da tenere accanto all'app (MIT + Apache-2.0 + NOTICE delle dipendenze Rust).
mkdir -p "$(dirname "$OUT")/LibTrueHD"
cp "$WORK/LibTrueHD/LICENSE" "$WORK/LibTrueHD/LICENSE-APACHE" "$WORK/LibTrueHD/NOTICE" "$(dirname "$OUT")/LibTrueHD/"
# Set an explicit module cache for builds in restricted environments.
meson setup "$WORK/build" "$WORK/mpv-0.42.0" -Dlibmpv=true -Dbuild-date=false \
    -Djavascript=enabled -Dlua=luajit -Dlibarchive=enabled -Duchardet=enabled -Dvulkan=enabled \
    "-Dswift-flags=-module-cache-path $WORK/swift-cache -target arm64-apple-macosx15.0" \
    "-Dc_args=-I$WORK/LibTrueHD/include" "-Dc_link_args=-Wl,-force_load,$THD_A" "-Dobjc_link_args=-Wl,-force_load,$THD_A"
meson compile -C "$WORK/build"
mkdir -p "$(dirname "$OUT")"
install -m 755 "$WORK/build/libmpv.2.dylib" "$OUT"
