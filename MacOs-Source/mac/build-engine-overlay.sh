#!/usr/bin/env bash
# Motore madVR / MPC Video Renderer per Mac: SOLO i pezzi nostri, che entrano nell'app (Contents/Resources/Engine).
# Tutto il resto l'app lo scarica dai siti ufficiali al primo avvio (MacEngine):
#   - MetalSharp Wine 6.0 ARM64 (github.com/metalsharp/MetalSharp-Wine, release v6.0)
#   - madVR (madshi.net), MPC Video Renderer, LAV Filters, XySubFilter (GitHub degli autori)
# e il prefisso di Windows lo crea Wine stesso (wineboot) sul Mac dell'utente.
#
# Pezzi nostri (presi dal motore di sviluppo dist/mac/runtime-arm64, quello provato):
#   - wined3d.dll, wined3d.so, winemac.so ricompilati con le patch di mac/wine-patches (build-fex-iosurface.sh)
#   - quartz.dll di DirectShow ricompilata (system32 del prefisso)
#   - impostazioni di madVR (settings.bin/.bak e il blocco "Settings" del registro), di LAV, MPC-VR e XySubFilter,
#     modalita' Retina del driver Mac di Wine: in cinecore-settings.reg
#   - CinecoreVideoToolbox.dll (decodifica HEVC con VideoToolbox)
# Il manifesto (mac/engine-manifest.json) fissa le impronte SHA-256 di tutto: archivi ufficiali, file presi da ognuno
# e pezzi nostri. Uso: bash mac/build-engine-overlay.sh [versione]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(date +%Y.%m.%d)}"
SRC="$ROOT/dist/mac/runtime-arm64"
APPNAME="MetalSharp-Wine-IOSurface.app"
P="$SRC/prefix-iosurface"
OUT="$ROOT/dist/engine-pack/overlay"
CACHE="$ROOT/dist/engine-pack/components-cache"
[[ -x "$SRC/$APPNAME/Contents/MacOS/wine" && -d "$P/drive_c" ]] || { echo "Manca il motore di sviluppo in $SRC" >&2; exit 1; }
rm -rf "$OUT"; mkdir -p "$OUT" "$CACHE"

python3 - "$ROOT" "$SRC" "$APPNAME" "$P" "$OUT" "$CACHE" "$VERSION" <<'PY'
import sys, os, json, hashlib, shutil, urllib.request, zipfile, re, pathlib
root, src, appname, prefix, out, cache, version = sys.argv[1:8]
def sha(data): return hashlib.sha256(data).hexdigest()
def sha_file(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""): h.update(c)
    return h.hexdigest()

overlay = []
def add(src_path, rel):
    dst = os.path.join(out, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src_path, dst)
    overlay.append({"path": rel, "sha256": sha_file(dst), "size": os.path.getsize(dst)})

lib = os.path.join(src, appname, "Contents", "lib", "wine")
for rel in ["aarch64-unix/wined3d.so", "aarch64-unix/winemac.so", "aarch64-windows/wined3d.dll"]:
    add(os.path.join(lib, rel), f"{appname}/Contents/lib/wine/{rel}")
add(os.path.join(prefix, "drive_c/windows/system32/quartz.dll"), "prefix/drive_c/windows/system32/quartz.dll")
for n in ["settings.bin", "settings.bak"]:
    p = os.path.join(prefix, "drive_c/Cinecore/madVR", n)
    if os.path.exists(p): add(p, f"prefix/drive_c/Cinecore/madVR/{n}")
add(os.path.join(src, "CinecoreVideoToolbox.dll"), "CinecoreVideoToolbox.dll")

# Impostazioni dal registro dell'utente del prefisso di sviluppo: solo i rami dei filtri e del driver Mac.
# Fuori: misure dello schermo di madVR, dispositivi audio, font, stampanti (cose della macchina di chi prepara).
keep = ["Software\\Gabest\\XySubFilter", "Software\\LAV", "Software\\MPC-BE Filters\\MPC Video Renderer", "Software\\Wine\\Mac Driver"]
exact = ["Software\\madshi\\madVR"]
text = open(os.path.join(prefix, "user.reg"), encoding="latin-1").read()
blocks = text.split("\n\n")
reg = ["REGEDIT4", ""]
for b in blocks:
    lines = b.strip("\n").split("\n")
    if not lines or not lines[0].startswith("["): continue
    key = lines[0][1:lines[0].index("]")].replace("\\\\", "\\")
    if not (any(key == k or key.startswith(k + "\\") for k in keep) or key in exact): continue
    values = [l for l in lines[1:] if not l.startswith("#")]
    if any("str(2):" in v or "str(7):" in v for v in values):
        sys.exit("valore str(2)/str(7) non gestito in " + key)
    reg.append("[HKEY_CURRENT_USER\\" + key + "]")
    reg.extend(values)
    reg.append("")
regfile = os.path.join(out, "cinecore-settings.reg")
open(regfile, "w", encoding="latin-1", newline="\r\n").write("\n".join(reg) + "\n")
overlay.append({"path": "cinecore-settings.reg", "sha256": sha_file(regfile), "size": os.path.getsize(regfile)})

# Componenti ufficiali: archivio (impronta fissata) e, per ognuno, i file che il motore usa (identici al prefisso).
components = []
for c in json.load(open(os.path.join(root, "mac", "engine-components.json")))["components"]:
    archive = pathlib.Path(cache, c["sha256"] + ".zip")
    if not archive.exists():
        data = urllib.request.urlopen(urllib.request.Request(c["url"], headers={"User-Agent": "CinecorePlayer-Build"}), timeout=120).read()
        if sha(data) != c["sha256"]: sys.exit(c["name"] + ": archivio ufficiale cambiato, aggiorna mac/engine-components.json")
        archive.write_bytes(data)
    files = []
    with zipfile.ZipFile(archive) as z:
        for info in z.infolist():
            if info.is_dir() or not info.filename.startswith(c["zipRoot"]): continue
            rel = info.filename[len(c["zipRoot"]):]
            target = os.path.join(prefix, c["dest"], rel)
            if not os.path.isfile(target): continue
            data = z.read(info)
            if sha(data) != sha_file(target): sys.exit(f"{c['name']}: {rel} diverso dal prefisso di sviluppo")
            files.append({"zip": info.filename, "path": c["dest"] + "/" + rel, "sha256": sha(data), "size": len(data)})
    components.append({"name": c["name"], "version": c["version"], "url": c["url"], "sha256": c["sha256"],
                       "size": archive.stat().st_size, "files": files})
    print(f"  {c['name']} {c['version']}: {len(files)} file")

# Ogni file della cartella C:\Cinecore del prefisso di sviluppo deve arrivare: da un archivio ufficiale o dai pezzi nostri.
have = {f["path"] for c in components for f in c["files"]} | {o["path"][len("prefix/"):] for o in overlay if o["path"].startswith("prefix/")}
for dp, dn, fn in os.walk(os.path.join(prefix, "drive_c", "Cinecore")):
    for n in fn:
        rel = os.path.relpath(os.path.join(dp, n), prefix)
        if rel not in have and not rel.endswith(".off"):
            print("  (non arriva, non serve al motore):", rel)

runtime = json.load(open(os.path.join(root, "mac", "engine-runtime.json")))
register = ["drive_c/Cinecore/LAV/LAVSplitter.ax", "drive_c/Cinecore/LAV/LAVVideo.ax", "drive_c/Cinecore/LAV/LAVAudio.ax",
            "drive_c/Cinecore/madVR/madVR64.ax", "drive_c/Cinecore/MPCVR/MpcVideoRenderer64.ax", "drive_c/Cinecore/XySubFilter/XySubFilter.dll"]
installed = 1_500_000_000  # misurato il 23/11 dopo la dieta (EngineSlim): ~1,5 GB
manifest = {"version": version, "runtime": runtime, "components": components, "overlay": overlay,
            "register": register, "installedSize": installed}
pathlib.Path(root, "mac", "engine-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"Pezzi nostri: {len(overlay)} file, {sum(o['size'] for o in overlay) / 1e6:.1f} MB in {out}")
PY
echo "Manifesto: $ROOT/mac/engine-manifest.json (lo copia build-native-app.sh insieme ai pezzi nostri)"
