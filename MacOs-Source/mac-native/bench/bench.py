"""Measure one VideoHost at a time and keep raw logs even when a case fails."""

import argparse
import csv
import os
from pathlib import Path
import re
import signal
from statistics import mean
import subprocess
import sys
import time


FIELDS = (
    "prova", "renderer", "dimensione", "file", "esito", "secondi_misurati",
    "disegnati", "persi", "fps_renderer", "cpu_media", "cpu_max", "present_s",
    "intervallo_medio_ms", "intervallo_max_ms", "sec_con_picchi_50ms", "copie_s", "copia_media_ms",
)
NUMBER = re.compile(r"(\S+)=([0-9]+(?:\.[0-9]+)?)")


def stop(host):
    if host.poll() is None:
        try:
            host.stdin.write(b"quit\n")
            host.stdin.flush()
        except BrokenPipeError:
            pass
    host.stdin.close()
    try:
        return host.wait(timeout=5)
    except subprocess.TimeoutExpired:
        host.terminate()
        try:
            return host.wait(timeout=2)
        except subprocess.TimeoutExpired:
            host.kill()
            return host.wait()


def play(path, renderer, rect, seconds, log_path):
    wine_path = "Z:" + str(path.resolve()).replace("/", "\\")
    command = [os.environ["WINE"], os.environ["BENCH_HOST"], "--file", wine_path,
               "--renderer", renderer, "--rect", rect]
    env = dict(os.environ, CINECORE_VIDEOHOST_MUTE="1")
    with log_path.open("wb") as log, log_path.open(encoding="utf-8", errors="replace") as reader, \
            log_path.with_suffix(".stderr").open("wb") as stderr:
        host = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=log, stderr=stderr, env=env)
        status = "avvio scaduto"
        started = False
        deadline = time.monotonic() + 30
        try:
            finished = False
            while time.monotonic() < deadline:
                for line in reader:
                    if line.startswith("ready "):
                        started = True
                        status = "ok"
                        deadline = time.monotonic() + seconds
                    elif line.startswith("error "):
                        status = "errore VideoHost"
                        finished = True
                        break
                    elif started and line.startswith("pos "):
                        _, position, duration, _ = line.split()
                        if float(duration) > 0 and float(position) >= float(duration) - 0.10:
                            status = "ok (fine clip)"
                            finished = True
                            break
                if finished:
                    break
                if host.poll() is not None:
                    status = f"uscita prematura ({host.returncode})"
                    break
                time.sleep(0.10)
        finally:
            code = stop(host)
        return status if code == 0 else f"chiusura con codice {code}"


def summarize(log_path, status):
    # Scarta avvio e ultimo campione, che potrebbe includere la fine della clip.
    lines = [line for line in log_path.read_text(errors="replace").splitlines()
             if line.startswith("stats ")]
    log_path.with_suffix(".stats").write_text("".join(line + "\n" for line in lines))
    rows = [{key: float(value) for key, value in NUMBER.findall(line)}
            for line in lines[3:-1]]
    result = {"esito": status, "secondi_misurati": len(rows)}
    if not status.startswith("ok"):
        return result
    if len(rows) < 3 or rows[-1]["pos"] <= rows[0]["pos"] or not any(
            row.get("present", 0) or row.get("disegnati", 0) for row in rows):
        result["esito"] = "campioni insufficienti o video fermo"
        return result
    for key, label in (("disegnati", "disegnati"), ("persi", "persi")):
        if key in rows[0] and key in rows[-1]:
            result[label] = int(rows[-1][key] - rows[0][key])
    result["fps_renderer"] = rows[-1].get("fps", "")
    for label, key, aggregate in (
        ("cpu_media", "cpu", mean), ("cpu_max", "cpu", max),
        ("present_s", "present", mean), ("copie_s", "copie", mean),
        ("intervallo_medio_ms", "intMedio", mean),
        ("intervallo_max_ms", "intMax", max), ("copia_media_ms", "copiaMedia", mean),
    ):
        values = [row[key] for row in rows if key in row]
        if values:
            result[label] = round(aggregate(values), 1)
    result["sec_con_picchi_50ms"] = sum(row.get(">50ms", 0) > 0 for row in rows)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("secondi", type=int, nargs="?", default=40)
    parser.add_argument("file", type=Path, nargs="*")
    args = parser.parse_args()
    files = args.file or sorted((Path.home() / "Movies/cinecore-test").glob("*.mkv"))
    renderers = os.environ.get("BENCH_RENDERERS", "madvr mpcvr").split()
    sizes = os.environ.get("BENCH_SIZES", "full window").split()
    if args.secondi < 8 or not files or any(not path.is_file() for path in files):
        parser.error("servono almeno 8 secondi e uno o più file locali esistenti")
    if not renderers or set(renderers) - {"madvr", "mpcvr"} or not sizes or set(sizes) - {"full", "window"}:
        parser.error('valori consentiti: BENCH_RENDERERS="madvr mpcvr", BENCH_SIZES="full window"')
    out = Path(os.environ["BENCH_OUT"])
    out.mkdir(parents=True, exist_ok=True)
    full_rect = f"0,0,{os.environ.get('BENCH_FULL_W', '2940')},{os.environ.get('BENCH_FULL_H', '1912')}"
    failed = False
    number = 0
    with (out / "risultati.csv").open("x", newline="") as csv_file, \
            (out / "RAPPORTO.txt").open("x") as report:
        writer = csv.DictWriter(csv_file, fieldnames=FIELDS)
        writer.writeheader()
        for path in files:
            for renderer in renderers:
                for size in sizes:
                    number += 1
                    name = f"{number:02d}-{renderer}-{size}-{path.stem}"
                    print(f"==> {name}", flush=True)
                    log_path = out / f"{name}.log"
                    rect = full_rect if size == "full" else "200,200,1280,720"
                    status = play(path, renderer, rect, args.secondi, log_path)
                    metrics = summarize(log_path, status)
                    failed |= not metrics["esito"].startswith("ok")
                    writer.writerow(dict(prova=name, renderer=renderer, dimensione=size,
                                         file=path.name, **metrics))
                    csv_file.flush()
                    details = "  ".join(f"{key}={value}" for key, value in metrics.items())
                    print(f"{name}\n  {details}", file=report, flush=True)
                    print(f"    {details}", flush=True)
    print(f"Risultati in {out}", flush=True)
    return int(failed)


if __name__ == "__main__":
    # Esegui il finally del processo figlio anche quando il benchmark viene interrotto.
    signal.signal(signal.SIGTERM, lambda *_: signal.raise_signal(signal.SIGINT))
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
