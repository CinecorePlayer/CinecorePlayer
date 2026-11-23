#!/usr/bin/env python3
"""Copia nel pacchetto .app i componenti nativi presi da Homebrew, cosi' l'app parte su un Mac senza Homebrew.

  Contents/Frameworks: libmpv.2.dylib + tutte le librerie da cui dipende (ffmpeg, libplacebo, libass, ...),
                       libMoltenVK.dylib + MoltenVK_icd.json (mpv disegna con Vulkan sopra Metal)
  Contents/MacOS:      ffmpeg, ffprobe (anteprima della barra del tempo, analisi dei file)

I riferimenti fra le librerie vengono riscritti (@loader_path / @executable_path) e ogni file viene rifirmato
ad hoc: install_name_tool invalida la firma e su Apple Silicon un dylib senza firma valida non si carica.
Uso: python3 mac/bundle-native-libs.py "<percorso>.app"
"""
import json
import os
import shutil
import subprocess
import sys

BREW = os.environ.get("HOMEBREW_PREFIX", "/opt/homebrew")
SYSTEM = ("/usr/lib/", "/System/")


def otool(args, path):
    return subprocess.run(["otool", *args, path], check=True, capture_output=True, text=True).stdout


def deps(path):
    lines = otool(["-L"], path).splitlines()[1:]
    out = [l.strip().split(" (")[0] for l in lines if l.strip()]
    # Per un dylib la prima riga e' il suo stesso nome (LC_ID_DYLIB).
    own = id_of(path)
    return [d for d in out if d != own]


def id_of(path):
    lines = otool(["-D"], path).splitlines()
    return lines[1].strip() if len(lines) > 1 else None


def rpaths(path):
    out, lines = [], otool(["-l"], path).splitlines()
    for i, l in enumerate(lines):
        if l.strip() == "cmd LC_RPATH":
            out.append(lines[i + 2].strip().split("path ", 1)[1].split(" (offset")[0])
    return out


def resolve(ref, owner):
    """Il file vero dietro un riferimento (@rpath, @loader_path, percorso assoluto)."""
    base = os.path.dirname(owner)
    if ref.startswith("@loader_path/") or ref.startswith("@executable_path/"):
        return os.path.realpath(os.path.join(base, ref.split("/", 1)[1]))
    if ref.startswith("@rpath/"):
        for rp in rpaths(owner):
            rp = rp.replace("@loader_path", base).replace("@executable_path", base)
            cand = os.path.join(rp, ref[len("@rpath/"):])
            if os.path.exists(cand):
                return os.path.realpath(cand)
        raise SystemExit(f"non trovo {ref} (da {owner})")
    return os.path.realpath(ref)


def is_system(ref):
    return ref.startswith(SYSTEM)


def main():
    app = os.path.abspath(sys.argv[1])
    macos, frameworks = os.path.join(app, "Contents", "MacOS"), os.path.join(app, "Contents", "Frameworks")
    os.makedirs(frameworks, exist_ok=True)

    icd_src = os.path.join(BREW, "etc", "vulkan", "icd.d", "MoltenVK_icd.json")
    icd = json.load(open(icd_src))
    moltenvk = os.path.realpath(os.path.join(os.path.dirname(icd_src), icd["ICD"]["library_path"]))

    libs = {}      # nome nel pacchetto -> sorgente
    mpv = os.environ.get("CINECORE_MPV_DYLIB") or os.path.join(BREW, "lib", "libmpv.2.dylib")
    queue = [os.path.realpath(mpv), moltenvk]
    # DVD: libdvdread e libdvdcss (protetti, MacDvdRead; libdvdcss la carica libdvdread da sola) e libdvdnav (menu, MacDvdNav).
    # Automix: Rubber Band (tempo dei brani senza cambiare l'intonazione, RubberBandStretcher).
    for extra in ("libdvdread.8.dylib", "libdvdcss.2.dylib", "libdvdnav.4.dylib", "librubberband.3.dylib"):
        path = os.path.join(BREW, "lib", extra)
        if os.path.exists(path): queue.append(os.path.realpath(path))
    tools = [os.path.realpath(shutil.which(t, path=os.path.join(BREW, "bin")) or "") for t in ("ffmpeg", "ffprobe")]
    tools += [os.path.realpath(path) for path in sys.argv[2:]]
    for t in tools:
        if not os.path.isfile(t):
            raise SystemExit("ffmpeg/ffprobe di Homebrew mancanti (brew install ffmpeg)")
    refs = {}      # file sorgente -> [(riferimento originale, sorgente risolta)]

    for t in tools:
        refs[t] = [(r, resolve(r, t)) for r in deps(t) if not is_system(r)]
        queue += [s for _, s in refs[t]]
    seen = set()
    while queue:
        src = queue.pop()
        if src in seen:
            continue
        seen.add(src)
        name = "libmpv.2.dylib" if src.endswith("/libmpv.2.dylib") else os.path.basename(src)
        if name in libs and libs[name] != src:
            raise SystemExit(f"due librerie diverse con lo stesso nome: {libs[name]} / {src}")
        libs[name] = src
        refs[src] = [(r, resolve(r, src)) for r in deps(src) if not is_system(r)]
        queue += [s for _, s in refs[src]]

    name_of = {src: name for name, src in libs.items()}

    def fix(dst, src, prefix):
        os.chmod(dst, 0o755)
        args = []
        for ref, target in refs[src]:
            args += ["-change", ref, prefix + name_of[target]]
        if args:
            subprocess.run(["install_name_tool", *args, dst], check=True, capture_output=True)
        for rp in rpaths(dst):
            subprocess.run(["install_name_tool", "-delete_rpath", rp, dst], check=False, capture_output=True)
        subprocess.run(["codesign", "--force", "--sign", "-", dst], check=True, capture_output=True)

    # libavcodec con i canali alti del DTS:X (mac/build-ffmpeg-dtsx.sh): stessa versione e dipendenze di quella di
    # Homebrew, si copia al suo posto (i riferimenti restano quelli dell'originale).
    overrides = {}
    for path in filter(None, (os.environ.get("CINECORE_AVCODEC_DYLIB"),)):
        if os.path.isfile(path):
            overrides[".".join(os.path.basename(path).split(".")[:2])] = path  # "libavcodec.63"
    for name, src in libs.items():
        dst = os.path.join(frameworks, name)
        override = overrides.get(".".join(name.split(".")[:2]))
        shutil.copy2(override or src, dst)
        if override:
            print(f"{name}: versione con i canali alti del DTS:X")
        os.chmod(dst, 0o755)
        subprocess.run(["install_name_tool", "-id", "@rpath/" + name, dst], check=True, capture_output=True)
        fix(dst, src, "@loader_path/")
    for t in tools:
        dst = os.path.join(macos, os.path.basename(t))
        if os.path.realpath(dst) != t:
            shutil.copy2(t, dst)
        fix(dst, t, "@executable_path/../Frameworks/")

    # Il caricatore Vulkan legge questo file (VK_DRIVER_FILES, impostato da MpvEngine): percorso relativo al json.
    icd["ICD"]["library_path"] = "./libMoltenVK.dylib"
    json.dump(icd, open(os.path.join(frameworks, "MoltenVK_icd.json"), "w"), indent=2)

    leftovers = []
    for f in [os.path.join(frameworks, n) for n in libs] + [os.path.join(macos, os.path.basename(t)) for t in tools]:
        leftovers += [f"{os.path.basename(f)} -> {d}" for d in deps(f) if d.startswith((BREW, "/usr/local"))]
    if leftovers:
        raise SystemExit("riferimenti a Homebrew rimasti:\n" + "\n".join(leftovers))
    size = sum(os.path.getsize(os.path.join(frameworks, n)) for n in os.listdir(frameworks))
    print(f"Componenti nativi: {len(libs)} librerie, ffmpeg + ffprobe, {size / 1e6:.0f} MB")


if __name__ == "__main__":
    main()
