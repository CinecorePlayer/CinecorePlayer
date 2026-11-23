#!/usr/bin/env python3
"""Confronta un motore installato dall'app (MacEngine, scaricato dai siti ufficiali) con quello di sviluppo provato.

Uso: python3 mac/verify-engine.py <motore installato> [<runtime di sviluppo>]
     (sviluppo predefinito: dist/mac/runtime-arm64, con MetalSharp-Wine-IOSurface.app e prefix-iosurface)

Controlla:
  1. Wine: ogni file identico byte per byte (dopo la dieta di mac/slim-engine.py / EngineSlim.cs: un file del motore
     di sviluppo ancora con i simboli di debug va bene se l'installato e' la sua versione alleggerita; le parti tolte
     dalla dieta possono mancare).
  2. Prefisso: ogni file identico (stessa regola della dieta), tranne Wine Mono (.NET di Wine, non serve al VideoHost) e cose della macchina di
     chi ha preparato quello di sviluppo (stampanti, cartelle utente).
  3. Registro: registrazioni dei filtri (CLSID e categoria dei filtri DirectShow) e impostazioni di madVR, LAV,
     MPC Video Renderer, XySubFilter e del driver Mac di Wine identiche.
Esce con 1 se qualcosa non torna.
"""
import hashlib
import os
import re
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
import importlib.util
_spec = importlib.util.spec_from_file_location("slim_engine", os.path.join(root, "mac", "slim-engine.py"))
slim = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(slim)
installed = sys.argv[1]
dev = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "dist", "mac", "runtime-arm64")
APP = "MetalSharp-Wine-IOSurface.app"
problems = []


def sha(p):
    if os.path.islink(p):
        return "L:" + os.readlink(p)
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def unsigned_sha(p):
    """Impronta di un eseguibile Mac senza la firma (la dieta cambia l'elenco dei file firmati, quindi la firma)."""
    import shutil, subprocess, tempfile
    with tempfile.TemporaryDirectory() as d:
        q = os.path.join(d, "f")
        shutil.copyfile(p, q)
        subprocess.run(["/usr/bin/codesign", "--remove-signature", q], capture_output=True)
        return sha(q)


def same(installed_file, dev_file):
    """Identici, oppure l'installato e' la versione alleggerita (senza debug) di quello di sviluppo."""
    a = sha(installed_file)
    if a == sha(dev_file): return True
    if os.path.islink(dev_file): return False
    with open(dev_file, "rb") as f: magic = f.read(4)
    if magic in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe"):
        return unsigned_sha(installed_file) == unsigned_sha(dev_file)
    with open(dev_file, "rb") as f: stripped = slim.strip_debug(f.read())
    return stripped is not None and hashlib.sha256(stripped).hexdigest() == a


def removed_by_slim(rel):
    rel = rel.replace(os.sep, "/")
    return any(rel == "Contents/" + r or rel.startswith("Contents/" + r + "/") for r in slim.REMOVED)


def tree(base):
    out = {}
    for dp, dn, fn in os.walk(base):
        for n in fn + [d for d in dn if os.path.islink(os.path.join(dp, d))]:
            p = os.path.join(dp, n)
            out[os.path.relpath(p, base)] = p
    return out


# 1. Wine
a, b = tree(os.path.join(installed, APP)), tree(os.path.join(dev, APP))
for r in sorted(set(a) | set(b)):
    if r not in a:
        if not removed_by_slim(r): problems.append("Wine: manca " + r)
    elif r not in b: problems.append("Wine: in piu' " + r)
    elif removed_by_slim(r): problems.append("Wine: non tolto dalla dieta " + r)
    elif r == "Contents/_CodeSignature/CodeResources": continue  # elenco firmato: cambia con la dieta (la firma la controlla codesign)
    elif not same(a[r], b[r]): problems.append("Wine: diverso " + r)
print(f"Wine: {len(a)} file controllati")
import subprocess
if subprocess.run(["/usr/bin/codesign", "--verify", "--deep", os.path.join(installed, APP)], capture_output=True).returncode != 0:
    problems.append("Wine: firma non valida")

# 2. Prefisso
skip = re.compile(r"(Microsoft\.NET/|windows/Installer/|spool/drivers/|^drive_c/users/|winsxs/manifests/.*mono|"
                  r"madVR/license\.txt$|\.off$|/lsteamclient\.dll$)", re.I)
a, b = tree(os.path.join(installed, "prefix", "drive_c")), tree(os.path.join(dev, "prefix-iosurface", "drive_c"))
a = {"drive_c/" + k: v for k, v in a.items()}
b = {"drive_c/" + k: v for k, v in b.items()}
checked = 0
for r in sorted(set(a) | set(b)):
    if skip.search(r): continue
    checked += 1
    if r not in a: problems.append("Prefisso: manca " + r)
    elif r not in b: problems.append("Prefisso: in piu' " + r)
    elif not same(a[r], b[r]): problems.append("Prefisso: diverso " + r)
print(f"Prefisso: {checked} file controllati")


# 3. Registro
def parse(path):
    keys, cur = {}, None
    lines = open(path, encoding="latin-1").read().split("\n")
    i = 0
    while i < len(lines):
        l = lines[i]
        if l.startswith("["):
            cur = l[1:l.index("]")]
            keys.setdefault(cur, {})
        elif cur is not None and l and l[0] in '"@':
            full = l
            while full.endswith("\\") and i + 1 < len(lines):
                i += 1
                full = full[:-1] + lines[i].strip()
            m = re.match(r'^(@|"(?:[^"\\]|\\.)*")=(.*)$', full)
            if m: keys[cur][m.group(1)] = m.group(2)
        i += 1
    return keys


filters = ["171252A0-8820-4AFE-9DF8-5C92B2D66B04", "2DFCB782-EC20-4A7C-B530-4577ADB33F21", "6B237877-902B-4C6C-92F6-E63169A5166C",
           "71F080AA-8661-4093-B15E-4F6903E77D0A", "B98D13E7-55DB-4385-A33D-09FD1BA26338", "E1A8B82A-32CE-4B0D-BE0D-AA68C772E423",
           "E8E73B6B-4CB3-44A4-BE99-4F7BCB96E491", "EE30215D-164F-4A92-A4EB-9D4C13390F9F"]
sysa, sysb = parse(os.path.join(installed, "prefix", "system.reg")), parse(os.path.join(dev, "prefix-iosurface", "system.reg"))
regs = 0
for k, v in sysb.items():
    if not any(f in k.upper() for f in filters): continue
    regs += 1
    if k not in sysa: problems.append("Registro di sistema: manca " + k)
    elif sysa[k] != v: problems.append("Registro di sistema: diverso " + k)
usra, usrb = parse(os.path.join(installed, "prefix", "user.reg")), parse(os.path.join(dev, "prefix-iosurface", "user.reg"))
mine = re.compile(r"^Software\\\\(Gabest\\\\XySubFilter|LAV|MPC-BE Filters\\\\MPC Video Renderer|Wine\\\\Mac Driver)(\\\\|$)|^Software\\\\madshi\\\\madVR$")
for k, v in usrb.items():
    if not mine.search(k): continue
    regs += 1
    if k not in usra: problems.append("Registro utente: manca " + k)
    elif usra[k] != v:
        diff = [n for n in v if usra[k].get(n) != v[n]]
        problems.append(f"Registro utente: diverso {k} ({', '.join(diff[:5])})")
print(f"Registro: {regs} chiavi controllate (filtri e impostazioni)")

if problems:
    print(f"\n{len(problems)} DIFFERENZE:")
    for p in problems[:80]: print("  " + p)
    sys.exit(1)
print("\nOK: motore installato identico a quello di sviluppo.")
