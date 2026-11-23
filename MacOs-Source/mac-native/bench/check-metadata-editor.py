"""Save a disposable title/year/poster through the real Avalonia editor, then remove only that test entry."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
DATA = Path.home() / "Library/Application Support/CinecorePlayer2025"
OUT = ROOT / "dist/bench/20261107-library-fixes"


def read(path):
    return json.loads(path.read_text()) if path.exists() else {}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manual_file = DATA / "metadataOverrides.json"
    index_file = DATA / "posterIndex.json"
    existed = manual_file.exists()
    baseline = read(manual_file)
    with tempfile.TemporaryDirectory(prefix="cinecore-metadata-check-") as folder:
        media = Path(folder) / "film-di-prova.mkv"
        media.touch()
        poster = ROOT / "Assets/Network/network-hero.jpg"
        script = f"1200:metadata-test:{media};2200:metadata-fields:Titolo di verifica|2026;2600:metadata-poster:{poster};4000:metadata-save;8000:trace;9500:quit"
        env = dict(os.environ, CINECORE_QA_NO_ACCOUNTS="1", CINECORE_LIBRARY_SOURCE="mac", CINECORE_QA_SCRIPT=script)
        try:
            with (OUT / "metadata-editor.log").open("w") as log:
                result = subprocess.run([str(ROOT / "dist/Cinecore Player (nativo).app/Contents/MacOS/CinecoreMac")],
                                        env=env, stdout=log, stderr=log, timeout=35)
            assert result.returncode == 0, f"App exited with {result.returncode}"
            data = read(manual_file)
            entry = data[str(media)]
            assert entry["Title"] == "Titolo di verifica"
            assert entry["Year"] == 2026
            saved_poster = Path(entry["PosterFile"])
            assert saved_poster.exists() and saved_poster.read_bytes() == poster.read_bytes()
            assert {k: v for k, v in data.items() if k != str(media)} == baseline, "Unrelated overrides changed"
            text = (OUT / "metadata-editor.log").read_text()
            assert "[qa] failed" not in text and "Unhandled exception" not in text
            assert "[qa] trace:" in text and "metadata=False" in text.split("[qa] trace:")[-1]
            print("PASS — title/year/poster saved through the editor; other corrections preserved")
        finally:
            data = read(manual_file)
            entry = data.pop(str(media), None)
            if entry:
                saved = Path(entry.get("PosterFile", ""))
                # The app generated this file for this unique temporary media path.
                if saved.is_file() and str(saved).startswith(str(DATA) + "/"):
                    saved.unlink()
            if not existed and not data:
                manual_file.unlink(missing_ok=True)
            elif manual_file.exists():
                manual_file.write_text(json.dumps(data, ensure_ascii=False, indent=2))
            index = read(index_file)
            items = index.get("Items", {})
            for key in list(items):
                if key.casefold() == str(media).casefold():
                    items.pop(key)
            if index_file.exists():
                index_file.write_text(json.dumps(index, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
