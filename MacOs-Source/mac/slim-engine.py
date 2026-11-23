#!/usr/bin/env python3
"""Dieta del motore madVR/MPC-VR: lo stesso identico procedimento di mac-native/CinecoreMac/EngineSlim.cs.

  1. via i simboli di debug dalle DLL di Wine (lib/wine/aarch64-windows e i386-windows);
  2. via Wine Mono, Gecko, archivi/asset di MetalSharp per i giochi, client di Steam;
  3. DLL del prefisso (system32, syswow64) uguali a quelle di Wine -> collegamenti fissi.

Uso: python3 mac/slim-engine.py <pacchetto Wine .app> [<prefisso>]
  es. motore installato:  .../Cinecore/engine/MetalSharp-Wine-IOSurface.app .../Cinecore/engine/prefix
      sviluppo:           dist/mac/runtime-arm64/MetalSharp-Wine-IOSurface.app dist/mac/runtime-arm64/prefix-iosurface
Nel prefisso di un motore nato prima della dieta le DLL sono copie non alleggerite: quelle uguali alla DLL di Wine
originale prendono la versione alleggerita (come se wineboot fosse partito dopo la dieta).
Dopo: firmare di nuovo il pacchetto (codesign --force --deep --sign -) se sono stati tolti file da Contents.
"""
import hashlib
import os
import shutil
import struct
import sys

REMOVED = [
    "share/wine/mono", "share/wine/gecko",
    "share/metalsharp/archives", "share/metalsharp/assets", "share/metalsharp/redist",
    "share/metalsharp/steam", "share/metalsharp/goldberg",
    "lib/wine/aarch64-windows/lsteamclient.dll", "lib/wine/i386-windows/lsteamclient.dll", "lib/wine/aarch64-unix/lsteamclient.so",
]
WINDOWS_DIRS = ["lib/wine/aarch64-windows", "lib/wine/i386-windows"]
PREFIX_DIRS = [("system32", "aarch64-windows"), ("syswow64", "i386-windows")]


def _name(b, o, strtab):
    raw = b[o:o + 8].split(b"\0", 1)[0].decode("ascii", "replace")
    if not raw.startswith("/") or strtab < 0:
        return raw
    try:
        s = strtab + int(raw[1:])
    except ValueError:
        return ""
    if s >= len(b):
        return ""
    e = b.find(b"\0", s)
    return b[s:e if e >= 0 else len(b)].decode("ascii", "replace")


def strip_debug(b):
    """Come EngineSlim.StripDebug: bytes alleggeriti oppure None (file da lasciare com'e')."""
    if len(b) < 0x40 or b[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<i", b, 0x3c)[0]
    if pe <= 0 or pe + 24 > len(b) or b[pe:pe + 4] != b"PE\0\0":
        return None
    n = struct.unpack_from("<H", b, pe + 6)[0]
    symtab, nsym = struct.unpack_from("<II", b, pe + 12)
    opt_size = struct.unpack_from("<H", b, pe + 20)[0]
    opt = pe + 24
    if opt + opt_size + n * 40 > len(b) or opt_size < 96:
        return None
    magic = struct.unpack_from("<H", b, opt)[0]
    if magic == 0x10b:
        dd = opt + 96
    elif magic == 0x20b:
        dd = opt + 112
    else:
        return None
    if dd + 40 <= opt + opt_size and struct.unpack_from("<II", b, dd + 32) != (0, 0):
        return None
    strtab = symtab + nsym * 18
    if symtab:
        if strtab + 4 > len(b):
            return None
        end = strtab + struct.unpack_from("<I", b, strtab)[0]
    else:
        end = -1
    sec = opt + opt_size
    keep = 0
    data_end = 0
    cut = None
    debug_end = 0
    image_end = 0
    in_debug = False
    for i in range(n):
        o = sec + i * 40
        name = _name(b, o, strtab if symtab else -1)
        if not name:
            return None
        vsize, va, rsize, rptr = struct.unpack_from("<IIII", b, o + 8)
        if name.startswith(".debug"):
            in_debug = True
            if rsize:
                cut = rptr if cut is None else min(cut, rptr)
                debug_end = max(debug_end, rptr + rsize)
        else:
            if in_debug or b[o:o + 1] == b"/":
                return None
            keep += 1
            if rsize:
                data_end = max(data_end, rptr + rsize)
            image_end = max(image_end, va + (vsize if vsize else rsize))
    if keep == n or cut is None or cut < data_end:
        return None
    if symtab:
        # dopo la tabella delle stringhe solo il riempimento a zero fino all'allineamento del file
        if symtab < debug_end or end > len(b) or b[end:].strip(b"\0"):
            return None
    elif debug_end != len(b):
        return None
    align = struct.unpack_from("<I", b, opt + 32)[0]
    if not align:
        return None
    out = bytearray(b[:cut])
    out[sec + keep * 40: sec + n * 40] = bytes((n - keep) * 40)
    struct.pack_into("<H", out, pe + 6, keep)
    struct.pack_into("<II", out, pe + 12, 0, 0)
    struct.pack_into("<I", out, opt + 56, (image_end + align - 1) // align * align)
    return bytes(out)


def sha_file(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def dir_size(p):
    t = 0
    for dp, dn, fn in os.walk(p):
        for n in fn:
            q = os.path.join(dp, n)
            if not os.path.islink(q):
                t += os.path.getsize(q)
    return t


def slim(app, prefix=None):
    contents = os.path.join(app, "Contents")
    saved = 0
    for rel in REMOVED:
        p = os.path.join(contents, rel)
        if os.path.isdir(p) and not os.path.islink(p):
            saved += dir_size(p)
            shutil.rmtree(p)
        elif os.path.isfile(p):
            saved += os.path.getsize(p)
            os.remove(p)
    # sha della DLL originale -> versione alleggerita (per il prefisso di un motore nato prima della dieta)
    originals = {}
    for d in WINDOWS_DIRS:
        base = os.path.join(contents, d)
        for dp, dn, fn in os.walk(base):
            for n in fn:
                p = os.path.join(dp, n)
                if os.path.islink(p):
                    continue
                with open(p, "rb") as f:
                    data = f.read()
                stripped = strip_debug(data)
                if stripped is None:
                    continue
                originals[(d, n.lower(), hashlib.sha256(data).hexdigest())] = p
                with open(p + ".slim", "wb") as f:
                    f.write(stripped)
                st = os.stat(p)
                os.chmod(p + ".slim", st.st_mode & 0o7777)
                os.replace(p + ".slim", p)
                saved += len(data) - len(stripped)
    print(f"Wine: {saved / 1e9:.2f} GB in meno")
    if prefix:
        saved_p = link_prefix(app, prefix, originals)
        print(f"Prefisso: {saved_p / 1e9:.2f} GB in meno")


def link_prefix(app, prefix, originals=None):
    saved = 0
    lib = os.path.join(app, "Contents", "lib", "wine")
    for win, wine in PREFIX_DIRS:
        wdir, ldir = os.path.join(prefix, "drive_c", "windows", win), os.path.join(lib, wine)
        if not os.path.isdir(wdir) or not os.path.isdir(ldir):
            continue
        by_name = {n.lower(): os.path.join(ldir, n) for n in os.listdir(ldir) if os.path.isfile(os.path.join(ldir, n))}
        for dp, dn, fn in os.walk(wdir):
            for n in fn:
                t = os.path.join(dp, n)
                s = by_name.get(n.lower())
                if not s or os.path.islink(t) or os.path.islink(s):
                    continue
                ts, ss = os.stat(t), os.stat(s)
                if (ts.st_dev, ts.st_ino) == (ss.st_dev, ss.st_ino) or ts.st_size == 0:
                    continue
                th = sha_file(t)
                same = ts.st_size == ss.st_size and th == sha_file(s)
                was_original = originals is not None and originals.get(("lib/wine/" + wine, n.lower(), th)) == s
                if not same and not was_original:
                    continue
                tmp = t + ".cinecore-link"
                os.link(s, tmp)
                os.replace(tmp, t)
                saved += ts.st_size
    return saved


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    slim(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
