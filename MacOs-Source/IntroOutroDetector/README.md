# TV Intro & Outro Detector

Fast, lightweight C# tool that automatically detects **opening credits (sigle)** and
**closing credits (titoli di coda)** across all episodes of a TV season.

Analyses a full season of 10–20 episodes in **under 2–3 minutes** on most hardware.

---

## Requirements

| Dependency | Notes |
|------------|-------|
| **.NET 8 SDK** | `dotnet --version` |
| **FFmpeg** | Must include `ffprobe`. Available at [ffmpeg.org](https://ffmpeg.org/download.html) |

FFmpeg must be on your system `PATH` (or pass `--ffmpeg` / `--ffprobe`).

---

## Build

```bash
cd IntroOutroDetector
dotnet build -c Release
# Binary: bin/Release/net8.0/IntroOutroDetector
```

Or publish as a self-contained single file:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# (also: linux-x64, osx-x64, osx-arm64)
```

---

## Usage

```
IntroOutroDetector [<season_directory>] [options]
```

### Examples

```bash
# Detect intro & outro in a season folder
IntroOutroDetector /media/BreakingBad/Season01

# Save results to JSON
IntroOutroDetector /media/BreakingBad/Season01 --output results.json

# Save results to CSV (for spreadsheets / Jellyfin / Plex tools)
IntroOutroDetector /media/BreakingBad/Season01 --output results.csv

# Use 8 threads, only detect intro
IntroOutroDetector /media/BreakingBad/Season01 --threads 8 --no-outro

# Verbose mode with custom FFmpeg path
IntroOutroDetector /media/Series/S02 --ffmpeg /usr/local/bin/ffmpeg -v

# Stricter matching (must match in 70% of episodes)
IntroOutroDetector /media/Series/S02 --min-match 0.7
```

### All options

| Option | Default | Description |
|--------|---------|-------------|
| `<season_dir>` | current dir | Directory containing episode video files |
| `--ffmpeg <path>` | `ffmpeg` | Path to ffmpeg binary |
| `--ffprobe <path>` | `ffprobe` | Path to ffprobe binary |
| `--intro-window <min>` | `10` | Minutes from episode start to search for intro |
| `--outro-window <min>` | `5` | Minutes from episode end to search for outro |
| `--threads <n>` | `4` | Parallel extraction threads (set to CPU count for max speed) |
| `--output <file>` | — | Save results to `.json` or `.csv` |
| `--no-intro` | — | Skip intro detection |
| `--no-outro` | — | Skip outro detection |
| `--min-match <0–1>` | `0.5` | Fraction of episodes that must agree on a segment |
| `--min-intro <sec>` | `25` | Minimum intro length in seconds |
| `--min-outro <sec>` | `20` | Minimum outro length in seconds |
| `--max-shift <frames>` | `120` | Max time shift tolerance between episodes (~60 s) |
| `-v / --verbose` | — | Show extra diagnostic info |

---

## How It Works

### Audio fingerprinting

1. FFmpeg extracts **mono 8 kHz PCM** audio from:
   - the first `--intro-window` minutes of each episode (intro search region)
   - the last `--outro-window` minutes of each episode (outro search region)
2. Each audio region is split into **512 ms frames** (4096 samples @ 8 kHz, no overlap).
3. Each frame is transformed with a **Cooley-Tukey FFT** (built-in, no dependencies).
4. The spectrum is divided into **32 frequency bands**, and a **32-bit hash** is derived
   from the spectral flux between adjacent bands (inspired by Chromaprint but 10× lighter).

### Cross-episode matching

5. Episode 2 is used as **reference** (avoids pilot/special-episode anomalies).
6. For every other episode the detector slides its fingerprint against the reference
   and finds the **offset that maximises frame matches** (Hamming distance < 8 bits).
   This handles **cold opens** of varying length transparently.
7. Each matching frame casts a **weighted vote** on the reference timeline.
8. The densest contiguous block of votes above the threshold becomes the
   **consensus segment** (intro or outro).
9. Per-episode timestamps are the consensus ± the individual cold-open offset.

### Performance

| Phase | Time (10-ep season, 4 threads) |
|-------|-------------------------------|
| Duration probing | ~2 s |
| Audio extraction + fingerprinting | 30–90 s |
| Cross-correlation matching | < 1 s |
| **Total** | **< 2 min** |

Extraction is the bottleneck; more `--threads` = proportionally faster.

---

## Output formats

### Console (always printed)

```
════════════════════════════════════════════════════════════════════════════════
  RESULTS — 10 episodes  (analysis: 87.4s)
════════════════════════════════════════════════════════════════════════════════

  Intro (consensus): 01:48 → 03:37  (109s)
  Outro (consensus): 40:15 → 44:02  (227s)

────────────────────────────────────────────────────────────────────────────────
  EPISODE                           INTRO                  OUTRO
────────────────────────────────────────────────────────────────────────────────
  S01E01 - Pilot.mkv                00:00→01:52 (112s)     40:10→44:00 (230s)
  S01E02 - Episode 2.mkv            01:48→03:37 (109s)     40:15→44:02 (227s)
  ...
```

### JSON (`--output results.json`)

```json
{
  "season_path": "/media/Show/Season01",
  "analysis_time_seconds": 87.4,
  "consensus_intro": { "start": 108.0, "end": 217.0, "length": 109.0 },
  "consensus_outro": { "start": 2415.0, "end": 2642.0, "length": 227.0 },
  "episodes": [
    {
      "filename": "S01E01 - Pilot.mkv",
      "duration": 2680.0,
      "intro": { "start": 0.0, "end": 112.0, "length": 112.0 },
      "outro": { "start": 2410.0, "end": 2640.0, "length": 230.0 }
    },
    ...
  ]
}
```

### CSV (`--output results.csv`)

```
Episode,IntroStart,IntroEnd,IntroLength,OutroStart,OutroEnd,OutroLength
"S01E01 - Pilot.mkv",0.00,112.00,112.00,2410.00,2640.00,230.00
...
```

---

## Tips & Troubleshooting

**No intro detected?**
- Lower `--min-match` (try `0.3`)
- Increase `--intro-window` if the intro appears after 10 minutes
- Some shows vary their intro placement significantly between episodes

**Wrong intro detected (theme song not found, random scene detected instead)?**
- Raise `--min-match` (try `0.7`)
- Try `--min-intro 60` if the intro is long (> 60 seconds)

**Analysis is slow?**
- Set `--threads` to your CPU core count
- Reduce `--intro-window` / `--outro-window` if the intro is early

**Episode files not found?**
- Supported extensions: `.mkv .mp4 .avi .m4v .mov .wmv .ts .m2ts`
- Files are sorted alphabetically — name them consistently (e.g., `S01E01_...`)

---

## License

MIT — free to use and modify.
