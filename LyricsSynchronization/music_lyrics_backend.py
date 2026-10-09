"""Cinecore's optional music-recognition worker.

This process deliberately returns ASR timing evidence only.  Cinecore keeps the
provider lyrics authoritative and performs the monotonic fuzzy alignment in C#.
The worker uses faster-whisper because it exposes word timing without requiring
the full PyTorch stack used by WhisperX.  Demucs is an opt-in preprocessing pass
for dense mixes; its output is cached by audio identity.

Install in an isolated Python environment:

    python -m pip install faster-whisper
    python -m pip install demucs       # optional, for --separate

The only stdout contract is one JSON object. Diagnostics and dependency errors
go to stderr so the host can reject a failed or empty recognition cleanly.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from typing import Any

# Redirected pipes on Windows otherwise use the legacy system code page. Lyrics
# and model diagnostics may contain any language, independently of UI locale.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")


def log(message: str) -> None:
    print(f"[cinecore-faster-whisper] {message}", file=sys.stderr, flush=True)


def choose_device(requested: str) -> str:
    if requested and requested.lower() != "auto":
        return requested
    try:
        import ctranslate2

        if ctranslate2.get_cuda_device_count() > 0:
            # A display driver alone is not the CUDA inference runtime.
            if os.name == "nt":
                ctypes.WinDLL("cublas64_12.dll")
                ctypes.WinDLL("cudnn64_9.dll")
            return "cuda"
        return "cpu"
    except Exception:
        return "cpu"


def choose_compute_type(requested: str, device: str) -> str:
    if requested and requested.lower() != "auto":
        return requested
    return "float16" if device.lower().startswith("cuda") else "int8"


def audio_identity(audio_path: Path, model: str) -> str:
    stat = audio_path.stat()
    value = f"{audio_path.resolve()}|{stat.st_size}|{stat.st_mtime_ns}|{model}"
    return hashlib.sha256(value.encode("utf-8", "surrogatepass")).hexdigest()


def separate_vocals(audio_path: Path, model: str) -> Path:
    """Return a cached Demucs vocal stem, or the original mix on failure."""
    try:
        import torch  # noqa: F401 - gives a useful early error on missing Demucs deps
    except Exception as exc:
        log(f"Demucs unavailable; using the original mix ({exc})")
        return audio_path

    root = Path(
        os.environ.get(
            "CINECORE_LYRICS_STEM_CACHE",
            str(Path(tempfile.gettempdir()) / "CinecoreLyricsStems"),
        )
    )
    cache_dir = root / audio_identity(audio_path, model)
    cached = list(cache_dir.rglob("vocals.wav")) if cache_dir.exists() else []
    if cached:
        return cached[0]

    cache_dir.mkdir(parents=True, exist_ok=True)
    command = [
        sys.executable,
        "-m",
        "demucs.separate",
        "-n",
        model,
        "--two-stems=vocals",
        "-o",
        str(cache_dir),
        str(audio_path),
    ]
    log(f"running optional Demucs separation ({model})")
    try:
        completed = subprocess.run(
            command,
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        if completed.returncode != 0:
            detail = (completed.stderr or completed.stdout).strip().splitlines()
            log("Demucs failed; using the original mix" + (f": {detail[-1]}" if detail else ""))
            return audio_path
        generated = list(cache_dir.rglob("vocals.wav"))
        if generated:
            return generated[0]
    except Exception as exc:
        log(f"Demucs failed; using the original mix ({exc})")
    return audio_path


def is_usable_word(text: str) -> bool:
    letters = [character.casefold() for character in text if character.isalnum()]
    # Instrumental passages can generate a single enormous, repeated Unicode
    # token with high model confidence. It is not usable timing evidence.
    return bool(letters) and not (len(letters) > 12 and len(set(letters)) <= 2)


def looks_like_garbage(words: list[dict[str, Any]]) -> bool:
    if len(words) < 8:
        return False
    normalized = [str(word["text"]).casefold() for word in words]
    # Whisper can hallucinate a repeated filler token over an instrumental
    # section. Do not reject a short repeated hook, only a long one-token loop.
    return len(set(normalized)) == 1 and len(normalized[0]) <= 6


def recognize(args: argparse.Namespace) -> dict[str, Any]:
    audio_path = Path(args.audio).expanduser().resolve()
    if not audio_path.is_file():
        raise RuntimeError(f"audio file does not exist: {audio_path}")

    started = time.perf_counter()
    input_path = separate_vocals(audio_path, args.demucs_model) if args.separate else audio_path
    device = choose_device(args.device)
    model_name = args.model
    if not model_name or model_name.lower() == "auto":
        # Large-v3 is the quality default where CUDA is available; small is a
        # materially more practical CPU fallback for a desktop player.
        model_name = "large-v3" if device.lower().startswith("cuda") else "small"
    compute_type = choose_compute_type(args.compute_type, device)

    try:
        from faster_whisper import WhisperModel
    except Exception as exc:
        raise RuntimeError(
            "faster-whisper is not installed; install it with 'python -m pip install faster-whisper'"
        ) from exc

    log(f"loading model={model_name} device={device} compute_type={compute_type}")
    model = WhisperModel(model_name, device=device, compute_type=compute_type,
                         cpu_threads=max(1, min(4, int(os.environ.get("CINECORE_LYRICS_CPU_THREADS", "2")))),
                         num_workers=1)
    language = os.environ.get("CINECORE_FASTER_WHISPER_LANGUAGE") or None
    kwargs: dict[str, Any] = {
        "word_timestamps": True,
        "vad_filter": bool(args.vad),
        "condition_on_previous_text": False,
        "beam_size": 3,
        "temperature": 0.0,
    }
    if language:
        kwargs["language"] = language
    if args.initial_prompt:
        kwargs["initial_prompt"] = args.initial_prompt
    if args.vad:
        kwargs["vad_parameters"] = {"min_silence_duration_ms": 500}

    segments, info = model.transcribe(str(input_path), **kwargs)
    words: list[dict[str, Any]] = []
    total_duration = max(1.0, float(getattr(info, "duration", 0.0) or 0.0))
    log("progress=0")
    for segment in segments:
        log(f"progress={min(1.0, float(segment.end) / total_duration):.3f}")
        for word in segment.words or []:
            text = str(getattr(word, "word", "") or "").strip()
            if not text or not is_usable_word(text):
                continue
            start = float(getattr(word, "start", 0.0) or 0.0)
            end = float(getattr(word, "end", start) or start)
            if not (start >= 0.0 and end >= start):
                continue
            probability = getattr(word, "probability", 1.0)
            try:
                confidence = max(0.0, min(1.0, float(probability)))
            except (TypeError, ValueError):
                confidence = 0.5
            words.append(
                {
                    "text": text,
                    "startSeconds": start,
                    "endSeconds": max(start, end),
                    "confidence": confidence,
                }
            )

    if not words:
        raise RuntimeError("faster-whisper returned no usable word timestamps")
    if looks_like_garbage(words):
        raise RuntimeError("faster-whisper returned a repeated-token hallucination")

    try:
        duration = float(getattr(info, "duration", 0.0) or 0.0)
    except (TypeError, ValueError):
        duration = 0.0
    if duration <= 0:
        duration = max(word["endSeconds"] for word in words)

    return {
        "backend": "faster-whisper-music",
        "model": model_name,
        "device": device,
        "computeType": compute_type,
        "durationSeconds": duration,
        "processingMilliseconds": (time.perf_counter() - started) * 1000.0,
        "words": words,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Cinecore faster-whisper music word-timing worker")
    parser.add_argument("--audio", required=True)
    parser.add_argument("--model", default="auto")
    parser.add_argument("--device", default="auto")
    parser.add_argument("--compute-type", default="auto")
    parser.add_argument("--initial-prompt", default="")
    parser.add_argument("--demucs-model", default="htdemucs")
    parser.add_argument("--separate", action="store_true")
    vad = parser.add_mutually_exclusive_group()
    vad.add_argument("--vad", dest="vad", action="store_true")
    vad.add_argument("--no-vad", dest="vad", action="store_false")
    parser.set_defaults(vad=False)
    return parser.parse_args()


def main() -> int:
    try:
        args = parse_args()
        audio = Path(args.audio).expanduser().resolve()
        cache_root = Path(os.environ.get("CINECORE_LYRICS_ASR_CACHE", str(Path(os.environ.get("APPDATA", tempfile.gettempdir())) / "CinecorePlayer2025" / "LyricsRecognition")))
        prompt_key = hashlib.sha256(args.initial_prompt.encode("utf-8")).hexdigest()[:16]
        identity = audio_identity(audio, f"v3|{args.model}|{args.device}|{args.compute_type}|{args.vad}|{args.separate}|{prompt_key}")
        cache_file = cache_root / f"{identity}.json"
        result = None
        if cache_file.exists():
            try:
                result = json.loads(cache_file.read_text(encoding="utf-8"))
                if not result.get("words") or any(not is_usable_word(str(w.get("text", ""))) for w in result["words"]):
                    result = None
            except (ValueError, OSError):
                result = None
        if result is None:
            result = recognize(args)
            try:
                cache_root.mkdir(parents=True, exist_ok=True)
                temp = cache_file.with_suffix(f".{os.getpid()}.tmp")
                temp.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
                temp.replace(cache_file)
            except OSError as exc:
                log(f"recognition cache could not be saved: {exc}")
        else:
            log("using saved word timestamps")
        json.dump(result, sys.stdout, ensure_ascii=False, separators=(",", ":"))
        sys.stdout.write("\n")
        return 0
    except KeyboardInterrupt:
        return 130
    except Exception as exc:
        log(str(exc))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
