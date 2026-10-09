# Cinecore music lyrics backend

The Cinecore desktop app invokes `music_lyrics_backend.py` only when the
optional Python environment is available. The worker is intentionally a small
process boundary: the .NET player remains responsible for cancellation,
caching, canonical lyrics, fuzzy monotonic alignment, confidence thresholds and
safe fallback.

Recommended setup:

```powershell
python -m venv .venv-cinecore-lyrics
.\.venv-cinecore-lyrics\Scripts\python.exe -m pip install faster-whisper
```

For dense commercial mixes, install `demucs` and opt in with
`CINECORE_LYRICS_USE_DEMUCS=1`. Demucs is not forced on every track because it
is substantially slower and can be unnecessary for an already vocal-forward
recording. The stem is cached using the audio file identity and separation
model.

The worker defaults to `vad_filter=False`: speech-oriented VAD can delete
slow, sustained sung phrases. Set `CINECORE_FASTER_WHISPER_VAD=1` for dense
rap or recordings where instrumental gaps dominate. The default `auto` model
selects `large-v3` with CUDA and `medium` for a reasonable CPU fallback.

The worker emits only word-timestamp JSON. It does not emit authoritative
lyrics and it rejects empty or obvious repeated-token hallucinations. Cinecore
then applies its existing fuzzy sequence alignment and refuses to replace plain
lyrics when coverage/confidence validation fails.
