# 🎬 Cinecore Player 2026

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements)
[![Development](https://img.shields.io/badge/development-Beta%202-yellow)](#-beta-2)
[![Status](https://img.shields.io/badge/status-experimental%20%7C%20not%20production--ready-orange)](#%EF%B8%8F-beta-2-limitations)
[![Downloads](https://img.shields.io/github/downloads-pre/CinecorePlayer/CinecorePlayer/total.svg)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/CinecorePlayer/CinecorePlayer?style=flat&logo=github)](https://github.com/CinecorePlayer/CinecorePlayer)
[![Languages](https://img.shields.io/badge/languages-English%20%7C%20Italian-4C9EEB)](#-localization)

Cinecore Player is a **free**, **source-available**, and **non-commercial** media player for Windows, built in **C# / .NET 9.0** and focused on high-quality local playback, home-cinema use and a modern media-center experience.

Cinecore combines a DirectShow-based playback path with **libmpv**, renderer support for **madVR**, **MPC Video Renderer (MPCVR)** and **EVR**, HDR handling, a TMDb-powered media library, its own **audio engine for music**, real-time audio analysis, browser remote control and Cinema Mode.

> ### 🚧 Development status
> **Beta 2 (version 0.2.0) is the current version of Cinecore Player.** It builds on Beta 1 and adds the Cinecore Audio Engine, native settings pages for the external renderers and filters, a reworked reviews page, a new installer and a refreshed interface.
>
> Beta 2 is still an **experimental development build and is not production-ready**. It has not been validated across the full range of GPUs, renderers, displays, audio devices and network setups. Bugs, renderer-specific behavior and rough edges should be expected.

---

## 📥 Download

Download **`CinecorePlayer-Beta2-Setup.exe`** from the [Releases page](https://github.com/CinecorePlayer/CinecorePlayer/releases) and run it.

The installer:

- runs in English or Italian and follows the Windows 11 light or dark theme;
- includes Cinecore, the .NET runtime, FFmpeg, libmpv and yt-dlp, so nothing else has to be installed first;
- lets you select the optional third-party components one by one: LAV Filters, MPC Audio Decoder, MPC Audio Renderer, madVR, MPC Video Renderer, XySubFilter and, only if it is missing, the WebView2 Runtime (downloaded from Microsoft);
- installs over a previous version, closing Cinecore if it is running;
- registers Cinecore in "Installed apps"; uninstalling also unregisters the filters and leaves your data in `%APPDATA%\CinecorePlayer2025`.

Administrator approval is required for the default Program Files location and for registering the filters. Some bundled third-party installers are unsigned and open with their own setup windows.

---

## 🆕 New in Beta 2

<!--
  Screenshots for this section go in Screenshots/beta2/ with the file names used below.
  Add the image with that exact name and it appears here; delete the line for any
  screenshot you decide not to include.
-->

### 🎚️ Cinecore Audio Engine

Music now plays through Cinecore's own engine: FFmpeg decoding, high-precision soxr resampling and a double-precision DSP chain, with shared or exclusive WASAPI output. In exclusive mode the device runs at the file's sample rate, bypassing the Windows mixer. The engine can be switched off to use mpv instead.

- Equalizer: 10-band graphic, 31-band third-octave graphic, or parametric (frequency, gain, Q and shape, including low and high shelf), edited directly on the graph
- ReplayGain per track or per album, with preamp
- Headphone crossfeed, stereo width, balance and mono sum
- Loudness compensation for low-volume listening
- Clip protection: true-peak limiter plus automatic headroom

![Cinecore Audio Engine settings](Screenshots/beta2/audio-engine.png)

![Parametric equalizer](Screenshots/beta2/equalizer.png)

### 📟 VU Meters & Analysis

Analog-style VU meters with automatic calibration that follows the loudness of the track, or fixed reference levels. Readouts cover true peak, clipping, loudness (LUFS), dynamics and limiter activity, and the analysis data can be exported. Each chart now explains what it shows.

![VU meters](Screenshots/beta2/vu-meters.png)

### 🎛️ Native Renderer & Filter Settings

madVR, LAV Video, LAV Audio, MPC Video Renderer and XySubFilter have settings pages inside Cinecore, in the same style as the player's own settings. madVR options such as chroma upscaling, NGU, image doubling, dithering, HDR handling and smooth motion can be changed without opening the madVR panel. The original panels remain available for everything else.

![madVR settings page](Screenshots/beta2/settings-madvr.png)

![MPC Video Renderer settings page](Screenshots/beta2/settings-mpcvr.png)

### ⭐ Ratings & Reviews

Movie and series pages show ratings from **IMDb**, **Letterboxd** and **Metacritic**, with Letterboxd and Metacritic reviews and spoiler warnings.

![Ratings and reviews](Screenshots/beta2/reviews.png)

### 🧪 Audio Synchronization

Automatic alignment compares several scenes of the two tracks, using audio correlation and, optionally, a local transcription. It detects tracks that run at a different speed (24 vs 23.976 fps, PAL 25 fps) and reports when a fixed delay cannot align them. The manual delay has a live preview, and nothing is saved before Apply.

![Audio synchronization](Screenshots/beta2/audio-sync.png)

### 📚 Library & Playlists

- New playlist sheet with name, description and cover; a playlist can mix films, series, music and photos
- Search by title, series or cast, and an "All sources" filter
- Empty libraries explain what to add; disconnected drives are shown as such
- Artist photos and album backgrounds in the music library
- TMDb and Spotify credentials are set in Settings, with a built-in key or a personal one

![New playlist sheet](Screenshots/beta2/playlist-sheet.png)

### 🖼️ Photos, Lyrics & Interface

- Reworked photo viewer: slideshow, wheel zoom, drag to pan, arrow-key navigation
- Genius added as a lyrics source; a new lyrics clock makes line changes and word highlighting smooth
- 3D playback through madVR and MPC Video Renderer, including 3D-to-2D, swap eyes and output format
- Subtitle position options: custom position, height on screen, moving subtitles into the black bars
- Spotlight is fully navigable from the phone remote, and the remote exposes the new audio and renderer settings
- New typography, anti-aliased rounded corners on Windows 11, smoother transitions and no banding in gradients
- The interface starts in Italian when Windows is in Italian, otherwise in English

![Photo viewer](Screenshots/beta2/photo-viewer.png)

![Browser remote, Spotlight navigation](Screenshots/beta2/remote-spotlight.png)

---

## 📸 Screenshots

The screenshots below were taken on Beta 1. The layout of these screens is the same in Beta 2; fonts, corners and some details have changed.

### 🏠 Home, Library & Discovery

#### Home

![Cinecore Player Home](Screenshots/Screenshot%202026-09-28%20105605.png)

#### Spotlight

**Spotlight** is a separate discovery surface for featured media. It presents each title with large backdrop artwork, year and runtime, video/HDR and audio-format badges, synopsis and cast, with direct **Play** and **Details** actions and a carousel to move between titles.

![Cinecore Player Spotlight](Screenshots/Screenshot%202026-09-28%20124315.png)

#### Film Library

![Cinecore Player Film Library](Screenshots/Screenshot%202026-09-28%20105926.png)

#### Movie Details

![Cinecore Player Movie Details](Screenshots/Screenshot%202026-09-28%20110447.png)

#### Viewing Diary

![Cinecore Player Viewing Diary](Screenshots/Screenshot%202026-09-28%20110338.png)

### 🎵 Music, Player & Audio Analysis

#### Music Home

![Cinecore Player Music Home](Screenshots/Screenshot%202026-09-28%20105955.png)

#### Album / Track View & Global Player

![Cinecore Player Album View](Screenshots/Screenshot%202026-09-28%20110024.png)

#### Real-Time Audio Analysis

![Cinecore Player Audio Analysis](Screenshots/Screenshot%202026-09-28%20110106.png)

#### Lyrics

![Cinecore Player Lyrics](Screenshots/Screenshot%202026-09-28%20110202.png)

### 🌐 Network & Remote Control

#### DLNA Server Selection

![Cinecore Player DLNA](Screenshots/Screenshot%202026-09-28%20110402.png)

#### Browser Remote

![Cinecore Player Browser Remote](Screenshots/WhatsApp%20Image%202026-09-28%20at%2011.11.36.jpeg)

### ⚙️ Settings

#### General Settings

![Cinecore Player Settings](Screenshots/Screenshot%202026-09-28%20110712.png)

---

## ✨ Features

### 🎞️ Media Library

Libraries for movies, TV series, videos, music and photos:

- Library scanning and search by title, series or cast
- **TMDb metadata**: artwork, details and cast
- Ratings and reviews from IMDb, Letterboxd and Metacritic
- Spotlight carousel for featured titles
- Favorites and viewing diary/history
- Resume playback and continue watching
- Playlists that can mix media types
- Editable playback queue, also from the playback overlay

### 🎥 Video Playback

Playback paths and renderers:

- **DirectShow** playback with **madVR**, **MPC Video Renderer (MPCVR)** or **EVR**
- **libmpv** playback
- Local files, network paths, media URLs and YouTube sources (through the bundled yt-dlp and FFmpeg)

Playback functions:

- Audio-track and subtitle-track selection
- HDR, 3D and upscaling options where the active renderer and hardware support them
- Bitstream output
- Native settings pages for madVR, LAV, MPC Video Renderer and XySubFilter
- Seek timeline with five previews
- Picture-in-picture mode
- On-screen controls, overlays and a playback-information panel
- Automatic refresh-rate switching

### 💬 Subtitles

- Track selection with an explicit **Off** state
- Forced-track selection based on language
- Position options: custom position, height on screen, subtitles in the black bars

### 🔊 Audio

- **Cinecore Audio Engine** for music, with equalizer, ReplayGain, crossfeed, stereo image controls, loudness compensation and clip protection
- Shared or exclusive WASAPI output
- PCM and bitstream output for video
- Audio-track selection
- Volume and mute control of a network receiver (Denon/Marantz, Onkyo/Integra/Pioneer eISCP, Yamaha MusicCast, UPnP/DLNA RenderingControl)

### 🧪 Audio / Video Synchronization

- Automatic alignment of an external or second audio track, based on audio correlation and optional local transcription
- Detection of tracks that run at a different speed
- Manual delay with live preview

These controls are **experimental**. Automatic alignment is not guaranteed to be correct for every file, dub or edit; check the result before relying on it.

### 🎵 Music Mode

- Global music player and music-library navigation
- Queue management
- Lyrics from LRCLIB, lyrics.ovh and Genius, with automatic synchronization
- Album and track metadata, artist photos and album backgrounds

### 📊 Real-Time Audio Analysis

- Waveform / oscilloscope
- Spectrum analyzer
- VU meters with true peak, clipping and limiter readouts
- Loudness (LUFS), RMS level and dynamics
- Phase correlation and channel balance
- Export of the analysis data

### 🎤 Lyrics Synchronization

Lyrics without timings are aligned to the audio in the background. Synchronization can still be off for alternate versions, live recordings, repeated choruses and songs whose structure differs from the reference lyrics. The Python backend is described in `LyricsSynchronizationTests/README.md`.

### 📱 Browser Remote Control

A browser-based remote for phones and tablets on the local network:

- Playback state and controls
- Queue and library access
- Audio-track and subtitle-track selection
- Spotlight navigation with D-pad, OK and Back
- Audio engine and renderer settings

### 🍿 Cinema Mode

- Pre-playback placeholder screen with the movie's artwork
- Optional pre-movie demo clip
- **WLED** integration for room-light control
- Automatic transition to the selected movie

### ⏭️ Skip Intro / Outro

TV-series playback can skip intros and outros and move on to the next episode.

### 🖼️ Photo Viewer

Photo browsing with slideshow, zoom, pan and keyboard navigation.

### 📡 DLNA & Network Media

DLNA/UPnP servers can be browsed and played alongside local files, network paths and URLs. Behavior varies with the network and the server.

### ⚙️ Settings

- Player, renderer and audio options
- TMDb and Spotify credentials
- Light and dark mode, accent color
- Italian and English interface

---

## 🌍 Localization

Cinecore Player supports two interface languages:

- 🇬🇧 **English**
- 🇮🇹 **Italian**

On first start the interface is Italian when Windows is in Italian and English otherwise. The language can be changed in the application settings.

---

## 🖥️ System Requirements

### End users

- **Operating system:** Windows x64. Rounded window corners and the Segoe UI Variable typeface require Windows 11.
- **Runtime:** none to install; the installer includes the .NET runtime.
- **WebView2 Runtime:** needed for WebView-backed pages; the installer offers it when it is missing.
- **Video renderers:** libmpv is bundled. madVR, MPC Video Renderer and LAV Filters are optional components offered by the installer. EVR is part of Windows.

Advanced video features depend on the selected renderer, the installed filters, the GPU drivers and the display.

### Building from source

Building Cinecore requires the **.NET 9 SDK**:

```powershell
dotnet restore CinecorePlayer2025.csproj
dotnet build CinecorePlayer2025.csproj -c Release
```

Run:

```text
bin/Release/net9.0-windows/CinecorePlayer2025.exe
```

Keep the **entire output directory**: the executable alone does not contain the native DLLs, filters, resources and helper tools the application needs. Optional backends must be in the expected `third-parties` locations or installed on the system.

To build the installer, install **Inno Setup 6.6 or later** and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

The installer and `SHA256SUMS.txt` are written to `artifacts\installer`.

---

## ⚠️ Beta 2 Limitations

Beta 2 is a development build, not finished software.

- Playback behavior can differ between madVR, MPCVR, EVR and libmpv
- HDR handling and refresh-rate switching need testing on more configurations
- The Cinecore Audio Engine is new; exclusive output depends on the audio device and driver
- Native renderer and filter pages cover the main options, not every setting of the original panels
- Synchronized lyrics can lose alignment on difficult tracks
- Automatic audio alignment is experimental
- Ratings, reviews, metadata and lyrics depend on external providers and on the network
- DLNA behavior depends on the server and the network
- Some bundled third-party installers are unsigned
- Long sessions and unusual media combinations need more stability testing

---

## 🚧 Beta 2

**Cinecore Player Beta 2 (0.2.0)** is the current development generation.

### Added in Beta 2

- Cinecore Audio Engine for music, with equalizer, ReplayGain, crossfeed, stereo image controls, loudness compensation and clip protection
- Shared and exclusive WASAPI output
- Analog-style VU meters and export of analysis data
- Native settings pages for madVR, LAV Video, LAV Audio, MPC Video Renderer and XySubFilter
- 3D playback through madVR and MPC Video Renderer
- Subtitle position options
- Ratings and reviews from IMDb, Letterboxd and Metacritic
- Multi-scene automatic audio alignment with speed-mismatch detection
- New playlist sheet, cast search, source filter and empty-library guidance
- Genius lyrics source and smoother lyrics timing
- Reworked photo viewer with slideshow
- Spotlight navigation and new settings pages in the browser remote
- New typography, rounded corners and smoother animations
- Startup language follows Windows
- New Inno Setup installer in English and Italian

### Carried over from Beta 1

- TMDb-integrated movie and TV library, Spotlight, favorites, viewing diary, resume
- Playlists and editable queue
- DirectShow and libmpv playback with madVR, MPCVR and EVR
- Local files, network paths, media URLs, YouTube and DLNA
- Audio and subtitle track controls, forced-subtitle handling
- HDR, bitstream and upscaling controls
- Five-preview seek timeline and picture-in-picture
- Global music player, lyrics and real-time audio analysis
- Browser remote control
- Cinema Mode with WLED
- Skip Intro / Outro
- Italian and English interface

Release notes for each version are on the [Releases page](https://github.com/CinecorePlayer/CinecorePlayer/releases).

---

## 🧪 Verification & Bug Reports

The codebase is checked with deterministic lyrics-synchronization tests, interface checks, measured DSP and decoder checks for the audio engine, and playback regression checks. These do not replace testing on real hardware, which is where reports help most.

When reporting a playback issue, please include:

- Windows version and display scaling
- GPU
- Renderer
- Audio output mode (shared, exclusive or bitstream) and device
- Media format and resolution
- Exact steps to reproduce the issue

Only include relevant log excerpts, and remove personal paths, tokens and credentials before posting them.

---

## 💡 Suggestions & Feedback

If you find a bug, have an idea for a feature or want to suggest an improvement, open an **Issue** on GitHub.

---

## 📄 License

Cinecore Player's original code is distributed under the **PolyForm Noncommercial 1.0.0 License**: it may be used, modified and studied under the conditions of the license, and commercial use is not permitted. See [`LICENSE`](LICENSE).

Third-party software keeps its own licenses. FFmpeg, libmpv and MPC Video Renderer are GPL-licensed. See [`THIRD-PARTY-CREDITS.md`](THIRD-PARTY-CREDITS.md) and the notices under `third-parties`.

---

## ⭐ Support the Project

If you like Cinecore Player, consider leaving a **⭐ Star** on the repository. It helps other users discover the project.

---

**Cinecore Player**  
*A modern Windows media player focused on playback quality, customization and the home-cinema experience.*
