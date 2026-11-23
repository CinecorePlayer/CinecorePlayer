"""Exercise VideoHost commands with private Wine/madVR state and muted audio."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]
OUT = Path(os.environ.get("BENCH_OUT", ROOT / "dist/bench/20261107-menu-comandi"))
HOST = Path(os.environ.get("BENCH_HOST", ROOT / "dist/mac-native/videohost-current/VideoHost.exe"))
SEED = Path(os.environ.get("BENCH_PREFIX_SEED", Path.home() / "Library/Application Support/Cinecore/wineprefix-w11"))
WINE_ROOT = ROOT / "dist/mac/runtime-wine11/wine"
WINE = WINE_ROOT / "bin/wine"
SERVER = WINE_ROOT / "bin/wineserver"
CLIP = Path.home() / "Movies/cinecore-test/test1080-audio-chapters.mkv"


def wine_path(path):
    return "Z:" + str(path.resolve()).replace("/", "\\")


def check(renderer, env, subtitle):
    log_path = OUT / (renderer + ".log")
    with log_path.open("w") as log, log_path.open() as reader, (OUT / (renderer + ".stderr")).open("w") as errors:
        host = subprocess.Popen([str(WINE), str(HOST), "--file", wine_path(CLIP), "--renderer", renderer,
                                 "--rect", "100,100,960,540"], stdin=subprocess.PIPE, stdout=log, stderr=errors, text=True, env=env)
        lines = []

        def wait_for(predicate, seconds=12):
            until = time.monotonic() + seconds
            while time.monotonic() < until:
                for line in reader:
                    lines.append(line.rstrip())
                    if line.startswith("error "):
                        raise AssertionError(line.rstrip())
                    if predicate(line):
                        return line
                if host.poll() is not None:
                    raise AssertionError("VideoHost exited before the expected reply")
                time.sleep(.05)
            raise AssertionError("Timed out waiting for a VideoHost reply")

        def send(command):
            host.stdin.write(command + "\n")
            host.stdin.flush()

        try:
            wait_for(lambda line: line.startswith("ready "), 40)
            wait_for(lambda line: line.startswith("pos ") and float(line.split()[1]) > .2)
            matches = [re.search(r"ritardo audio LAV: (-?\d+) ms", line) for line in lines]
            base = next((int(m[1]) for m in matches if m), 0)
            send("audio-offset 100")
            wait_for(lambda line: f"ritardo audio LAV: {base + 100} ms" in line)
            send("audio-offset 0")
            wait_for(lambda line: f"ritardo audio LAV: {base} ms" in line)
            send("subtitle " + wine_path(subtitle))
            wait_for(lambda line: "sottotitoli esterni caricati" in line)
            send("subsoff")
            wait_for(lambda line: line.startswith("tracks"))
            send("pause")
            wait_for(lambda line: line.startswith("pos ") and line.rstrip().endswith("paused"))
            send("seek 5")
            wait_for(lambda line: line.startswith("pos ") and abs(float(line.split()[1]) - 5) < .1)
            send("play")
            wait_for(lambda line: line.startswith("pos ") and line.rstrip().endswith("playing") and float(line.split()[1]) > 5.1)
            send("seek 39")
            wait_for(lambda line: line.rstrip() == "ended")
            # No duplicate EOF; seeking back must let a second completion reach the app.
            send("seek 38")
            send("play")
            wait_for(lambda line: line.startswith("pos ") and line.rstrip().endswith("playing"))
            wait_for(lambda line: line.rstrip() == "ended")
            print(renderer + ": PASS — audio offset, external subtitle, off, pause, seek, play, EOF, replay", flush=True)
        finally:
            if host.poll() is None:
                send("quit")
                try:
                    host.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    host.kill()
                    host.wait()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cinecore-menu-check-") as folder:
        qa = Path(folder)
        subprocess.run(["ditto", str(SEED), str(qa / "prefix")], check=True)
        subprocess.run(["ditto", str(ROOT / "dist/mac/app/third-parties/madVR09217"), str(qa / "madVR")], check=True)
        env = dict(os.environ, WINEPREFIX=str(qa / "prefix"), WINEDEBUG="-all", WINEARCH="win64", WINEMSYNC="1",
                   DXMT_LOG_LEVEL="none", DXMT_SHADER_CACHE_PATH=str(qa / "dxmt"), CINECORE_VIDEOHOST_MUTE="1",
                   DYLD_FALLBACK_LIBRARY_PATH=str(WINE_ROOT / "lib") + ":/usr/lib:/usr/local/lib")
        subtitle = qa / "sottotitoli esterni.srt"
        subtitle.write_text("1\n00:00:00,000 --> 00:00:30,000\nCinecore — sottotitoli esterni\n", encoding="utf-8")
        try:
            subprocess.run([str(WINE), "regsvr32", "/s", wine_path(qa / "madVR/madVR64.ax")], check=True, env=env,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=40)
            for renderer in ("madvr", "mpcvr"):
                check(renderer, env, subtitle)
        finally:
            subprocess.run([str(SERVER), "-k"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
            subprocess.run([str(SERVER), "-w"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)


if __name__ == "__main__":
    main()
