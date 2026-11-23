#!/usr/bin/env bash
# SUPERATO (20/11): il motore non si pubblica piu' come pacchetto. Wine e i filtri li scarica l'app dai siti ufficiali,
# i pezzi nostri sono nell'app: vedi mac/build-engine-overlay.sh e mac/verify-engine.py.
# Pacchetto del "motore Windows" (madVR, MPC Video Renderer, LAV Filters, XySubFilter sotto Wine ARM64) che l'app per
# Mac scarica a richiesta, come i componenti dell'installer di Windows. Non entra nel .dmg (circa 6 GB da installato).
#
# Contenuto: runtime Wine ARM64 (MetalSharp-Wine-IOSurface.app, con FEX), prefisso con i filtri gia' registrati,
# CinecoreVideoToolbox.dll (decodifica HEVC con VideoToolbox) e il VideoHost. I file dei filtri (madVR, MPC-VR, LAV,
# XySubFilter) NON ci sono: l'app li scarica dai siti degli autori (mac/engine-components.json, "components" nel manifesto).
# Il prefisso viene RIPULITO: niente nome utente (segnaposto @@USER@@), niente percorsi della macchina di build
# (segnaposto @@ENGINE@@), niente collegamenti alle cartelle dell'utente, niente lettere di unita' dei dischi, cache e log.
# L'app sostituisce i segnaposto al primo avvio (MacEngine.cs).
#
# Uscita: dist/engine-pack/<versione>/cinecore-engine-<versione>.aar.partNN (parti da 1,9 GB, limite di GitHub 2 GB)
# e mac/engine-manifest.json (versione, parti, SHA-256) che build-native-app.sh copia nell'app: l'app accetta solo parti
# con quelle impronte. Lo script NON pubblica nulla: stampa il comando gh da lanciare a mano.
# Uso: bash mac/build-engine-pack.sh [versione]   (predefinita: data di oggi, es. 2026.11.15)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(date +%Y.%m.%d)}"
SRC="$ROOT/dist/mac/runtime-arm64"
HOST="$ROOT/dist/mac-native/videohost-current"
OUT="$ROOT/dist/engine-pack/$VERSION"
STAGE="$OUT/stage/engine"
[[ -x "$SRC/MetalSharp-Wine-IOSurface.app/Contents/MacOS/wine" ]] || { echo "Manca il runtime: $SRC/MetalSharp-Wine-IOSurface.app" >&2; exit 1; }
[[ -f "$HOST/VideoHost.exe" ]] || { echo "Manca il VideoHost: $HOST" >&2; exit 1; }
rm -rf "$OUT"; mkdir -p "$STAGE"

echo "Copio runtime, VideoHost e ponte VideoToolbox…"
ditto "$SRC/MetalSharp-Wine-IOSurface.app" "$STAGE/MetalSharp-Wine-IOSurface.app"
cp "$SRC/CinecoreVideoToolbox.dll" "$STAGE/"
ditto "$HOST" "$STAGE/videohost"
rm -f "$STAGE"/videohost/*.log "$STAGE"/videohost/*.pdb 2>/dev/null || true

echo "Copio e ripulisco il prefisso…"
P="$STAGE/prefix"
ditto "$SRC/prefix-iosurface" "$P"
rm -rf "$P/cinecore-shaders" "$P"/*.log
# Solo C: e Z:; le altre lettere puntavano ai dischi di chi ha preparato il pacchetto.
find "$P/dosdevices" -mindepth 1 -maxdepth 1 ! -name 'c:' ! -name 'z:' -exec rm -f {} +
BUILD_USER="$(ls "$P/drive_c/users" | grep -v '^Public$' | head -1)"
if [[ -n "$BUILD_USER" ]]; then
  mv "$P/drive_c/users/$BUILD_USER" "$P/drive_c/users/@@USER@@"
  U="$P/drive_c/users/@@USER@@"
  # Collegamenti alle cartelle vere (Scrivania, Documenti, …) -> cartelle vuote; via le cache del profilo.
  find "$U" -maxdepth 1 -type l -exec sh -c 'rm -f "$1" && mkdir -p "$1"' _ {} \;
  rm -rf "$U/AppData/Local/dxvk" "$U/AppData/Local/Temp"/* 2>/dev/null || true
fi
python3 - "$P" "$BUILD_USER" "$SRC" <<'PY'
import sys, pathlib, re
prefix, user, src = sys.argv[1], sys.argv[2], sys.argv[3]
# Percorso del runtime come lo scrive Wine nel registro: Z:\\Users\\…\\runtime-arm64
win = "Z:" + src.replace("/", "\\")
reg_src = win.replace("\\", "\\\\")
for name in ("system.reg", "user.reg", "userdef.reg"):
    f = pathlib.Path(prefix, name)
    if not f.exists(): continue
    text = f.read_text(encoding="utf-8", errors="surrogateescape")
    text = text.replace(reg_src, "@@ENGINE@@")
    if user:
        text = re.sub(r"(?i)(\\\\users\\\\)" + re.escape(user) + r"(?=\\\\|\")", r"\1@@USER@@", text)
        text = re.sub(r'(?i)("USERNAME"=")' + re.escape(user) + '"', r'\1@@USER@@"', text)
    f.write_text(text, encoding="utf-8", errors="surrogateescape")
    left = [l for l in text.splitlines() if user and user.lower() in l.lower()]
    if left:
        print(f"ATTENZIONE: in {name} resta il nome utente in {len(left)} righe:", *left[:5], sep="\n  ")
        sys.exit(1)
PY
if LC_ALL=C grep -rIl "$BUILD_USER" "$P" --exclude-dir=windows 2>/dev/null | head -3 | grep -q .; then
  echo "ATTENZIONE: nome utente ancora presente in file del prefisso:" >&2
  LC_ALL=C grep -rIl "$BUILD_USER" "$P" --exclude-dir=windows | head >&2; exit 1
fi
printf '%s\n' "$VERSION" > "$STAGE/engine-version.txt"

# Componenti di terze parti (mac/engine-components.json): come sull'installer di Windows si scaricano dai siti degli
# autori. Dal pacchetto escono i file identici agli archivi ufficiali; il manifesto dice da quale archivio prenderli,
# dove metterli nel prefisso e l'impronta di ognuno. Restano nel pacchetto solo i file nostri (impostazioni di madVR).
echo "Tolgo dal pacchetto i componenti scaricati dai siti ufficiali…"
CACHE="$ROOT/dist/engine-pack/components-cache"
mkdir -p "$CACHE"
python3 - "$ROOT/mac/engine-components.json" "$CACHE" "$P" "$OUT/components.json" <<'PY'
import sys, json, hashlib, pathlib, urllib.request, zipfile
spec, cache, prefix, out = sys.argv[1:5]
def sha(data): return hashlib.sha256(data).hexdigest()
result = []
for c in json.load(open(spec))["components"]:
    archive = pathlib.Path(cache, c["sha256"] + ".zip")
    if not archive.exists():
        req = urllib.request.Request(c["url"], headers={"User-Agent": "CinecorePlayer-Build"})
        data = urllib.request.urlopen(req, timeout=120).read()
        if sha(data) != c["sha256"]:
            sys.exit(f"{c['name']}: l'archivio ufficiale e' cambiato (impronta diversa): aggiorna mac/engine-components.json")
        archive.write_bytes(data)
    files = []
    with zipfile.ZipFile(archive) as z:
        for info in z.infolist():
            if info.is_dir() or not info.filename.startswith(c["zipRoot"]): continue
            rel = info.filename[len(c["zipRoot"]):]
            target = pathlib.Path(prefix, c["dest"], rel)
            if not target.is_file(): continue
            data = z.read(info)
            if sha(data) != sha(target.read_bytes()): continue  # file cambiato da noi: resta nel pacchetto
            files.append({"zip": info.filename, "path": c["dest"] + "/" + rel, "sha256": sha(data), "size": len(data)})
            target.unlink()
    if not files: sys.exit(f"{c['name']}: nessun file del prefisso coincide con l'archivio ufficiale")
    print(f"  {c['name']} {c['version']}: {len(files)} file dal sito ufficiale")
    result.append({"name": c["name"], "version": c["version"], "url": c["url"], "sha256": c["sha256"],
                   "size": archive.stat().st_size, "files": files})
pathlib.Path(out).write_text(json.dumps(result, indent=2))
PY

echo "Archivio (Apple Archive, lzfse)…"
ARCHIVE="$OUT/cinecore-engine-$VERSION.aar"
aa archive -d "$OUT/stage" -o "$ARCHIVE" -a lzfse
TOTAL_SHA="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
TOTAL_SIZE="$(stat -f %z "$ARCHIVE")"
split -b 1900m -d -a 2 "$ARCHIVE" "$ARCHIVE.part"
rm -f "$ARCHIVE"
INSTALLED_SIZE="$(du -sk "$OUT/stage" | awk '{print $1 * 1024}')"
rm -rf "$OUT/stage"

python3 - "$OUT" "$VERSION" "$TOTAL_SHA" "$TOTAL_SIZE" "$INSTALLED_SIZE" "$ROOT/mac/engine-manifest.json" <<'PY'
import sys, json, hashlib, pathlib
out, version, total_sha, total_size, installed, manifest = sys.argv[1:7]
components = json.loads(pathlib.Path(out, "components.json").read_text())
installed = str(int(installed) + sum(f["size"] for c in components for f in c["files"]))
parts = []
for p in sorted(pathlib.Path(out).glob("*.aar.part*")):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
    parts.append({"name": p.name, "size": p.stat().st_size, "sha256": h.hexdigest()})
data = {
    "version": version,
    "tag": "mac-engine-v" + version,
    "baseUrl": "https://github.com/CinecorePlayer/CinecorePlayer/releases/download/mac-engine-v" + version + "/",
    "archiveSha256": total_sha, "archiveSize": int(total_size), "installedSize": int(installed),
    "parts": parts,
    "components": components,
}
pathlib.Path(manifest).write_text(json.dumps(data, indent=2) + "\n")
print(json.dumps(data, indent=2))
PY
echo
echo "Pacchetto pronto in $OUT. Per pubblicarlo (release separata, tag mac-engine-v$VERSION):"
echo "  gh release create mac-engine-v$VERSION \"$OUT\"/*.aar.part* -R CinecorePlayer/CinecorePlayer --title \"Motore madVR/MPC-VR per Mac $VERSION\" --notes \"Componente scaricato dall'app per Mac.\""
echo "Poi ricostruisci l'app (mac/build-native-app.sh): mac/engine-manifest.json entra nel pacchetto."
