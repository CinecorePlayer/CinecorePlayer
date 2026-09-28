# 🎬 Cinecore Player 2026

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements)
[![Development](https://img.shields.io/badge/development-Beta%20V1-yellow)](#-beta-v1)
[![Status](https://img.shields.io/badge/status-experimental%20%7C%20not%20production--ready-orange)](#%EF%B8%8F-beta-v1-limitations)
[![Downloads](https://img.shields.io/github/downloads-pre/NicoLando024/CinecorePlayer/total.svg)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/CinecorePlayer/CinecorePlayer?style=flat&logo=github)](https://github.com/CinecorePlayer/CinecorePlayer)
[![Languages](https://img.shields.io/badge/languages-English%20%7C%20Italian-4C9EEB)](#-localization)

Cinecore Player is a **free**, **source-available**, and **non-commercial** media player for Windows, built in **C# / .NET 9.0** and focused on high-quality local playback, home-cinema use and a modern media-center experience.

Cinecore combines a DirectShow-based playback path with **libmpv**, advanced renderer support including **madVR**, **MPC Video Renderer (MPCVR)** and **EVR**, intelligent HDR handling, a TMDb-powered media library, Audio Mode, browser remote control, Cinema Mode and a growing set of media-center features.

> ### 🚧 Development status
> **Beta V1 is the current version of Cinecore Player.** The previous Alpha has been retired and no longer represents or defines the current project.
>
> **Compared with the old Alpha, Beta V1 is years ahead in interface, feature set and overall functionality.** It is effectively a major generation jump for Cinecore rather than a small incremental update.
>
> That does **not** mean Beta V1 is finished. It is still an **experimental development build and is not production-ready**. The application is being optimized, polished and validated across different GPUs, renderers, displays, audio configurations and network environments. Bugs, renderer-specific behavior, performance issues, incomplete edge-case handling and experimental features should still be expected.

---

## 📸 Beta V1 Screenshots

The gallery below shows the **current Beta V1 interface**. All screenshots come from the present development generation and are grouped by the part of Cinecore they represent.

### 🏠 Home, Library & Discovery

#### Home

![Cinecore Player Home](Screenshots/Screenshot%202026-09-28%20105605.png)

#### Film Library

![Cinecore Player Film Library](Screenshots/Screenshot%202026-09-28%20105926.png)

#### Movie Details

![Cinecore Player Movie Details](Screenshots/Screenshot%202026-09-28%20110447.png)

#### Viewing Diary

![Cinecore Player Viewing Diary](Screenshots/Screenshot%202026-09-28%20110338.png)

---

### 🎵 Music, Player & Audio Analysis

#### Music Home

![Cinecore Player Music Home](Screenshots/Screenshot%202026-09-28%20105955.png)

#### Album / Track View & Global Player

![Cinecore Player Album View](Screenshots/Screenshot%202026-09-28%20110024.png)

#### Real-Time Audio Analysis

![Cinecore Player Audio Analysis](Screenshots/Screenshot%202026-09-28%20110106.png)

#### Lyrics

![Cinecore Player Lyrics](Screenshots/Screenshot%202026-09-28%20110202.png)

---

### 🌐 Network & Remote Control

#### DLNA Server Selection

![Cinecore Player DLNA](Screenshots/Screenshot%202026-09-28%20110402.png)

#### Browser Remote

![Cinecore Player Browser Remote](Screenshots/WhatsApp%20Image%202026-09-28%20at%2011.11.36.jpeg)

---

### ⚙️ Settings

#### General Settings

![Cinecore Player Settings](Screenshots/Screenshot%202026-09-28%20110712.png)

---

## 📌 Project Status

Cinecore Player is in **active Beta V1 development**.

The previous Alpha has been retired. **Beta V1 is now the version that represents the project**, and it has moved dramatically beyond the old Alpha with a redesigned interface, broader media-center functionality, deeper audio and video controls, improved navigation, more complete library workflows and a much larger set of playback features.

The objective of the current phase is no longer simply to add basic functionality. Most major user-facing systems are already present; work is now focused on **optimization, reliability, compatibility, edge cases, renderer-specific behavior, UI polish and real-world validation**.

### Where Beta V1 stands today

Beta V1 has reached the point where most of the **main Cinecore experience is already there**. The library, photo browsing, queue, music navigation, playback overlays, subtitle controls, audio-analysis pages and browser remote are all present and usable in the current build.

The audio-analysis side is now connected to the real PCM path rather than being just a visual prototype, while video playback already includes working seek controls, previews, overlays and track management. Subtitle handling has also been cleaned up significantly, including the explicit Off state and forced-subtitle selection by language.

The areas that still need the most work are the ones that depend heavily on the user's setup. **madVR, HDR, refresh-rate switching, GPU/driver combinations, displays and external filters** can still behave differently from one machine to another, so broader real-world testing is still needed. Synchronized lyrics are much better than before but can still lose alignment with live or alternate versions, repeated choruses and difficult vocal detection. Online metadata and services naturally remain dependent on the network and on the external providers behind them.

So while **Beta V1 is dramatically further ahead than the old Alpha**, it is still a beta in the literal sense: there are bugs to find, performance to improve, rough edges to polish and hardware combinations that have not been tested yet. It is **not production-ready**, but the core of the project is now much closer to the Cinecore I actually want to build.

---

## ✨ Beta V1 Features

### 🎞️ Media Library

The library supports movies, TV series, video, music and photos, with features including:

- Library scanning and search
- Cover artwork and media presentation
- Movie and TV detail pages
- Cast information
- **TMDb metadata integration**
- Favorites
- Viewing diary/history
- Resume playback
- Playlists
- Editable playback queue
- Queue management directly from the overlay

The primary library, photo, queue and music-navigation flows have already been exercised through interface tests in the current Beta V1 codebase.

---

### 🎥 Video Playback

Cinecore supports multiple playback paths and renderers:

- **DirectShow** playback
- **libmpv** playback
- **madVR**
- **MPC Video Renderer (MPCVR)**
- **EVR**
- Local files
- Network paths
- Media URLs
- YouTube sources

Playback-related functionality also includes:

- Audio-track selection
- Subtitle-track selection
- Forced subtitles based on language
- HDR options where supported by the active backend
- Bitstream output
- Upscaling options where supported by the active backend
- Seek and native player input handling
- Picture-in-picture mode
- Five-preview seek timeline
- On-screen playback controls and overlays

Core playback is usable and substantially more developed than in the old Alpha, but renderer-specific behavior is still one of the main areas requiring broader real-world testing.

---

### 🖥️ HUD / On-Screen Display

Beta V1 introduces a major redesign of Cinecore's playback HUD and overlays.

The current implementation includes playback controls, media information, track controls, seek behavior and preview handling directly over the video surface. Native input, seeking, 16:9 overlay geometry and test-video decoding have been specifically checked in the Beta V1 verification pass.

Further visual polish, responsiveness work and renderer-specific validation are still ongoing.

---

### 💬 Subtitles & Language Handling

Subtitle management includes:

- Subtitle track selection
- Explicit **Off** state
- Forced-track selection based on language
- Selection and disabling during playback

The forced-subtitle and Off-state logic has been corrected for Beta V1, and selection/disabling have also been tested against real **libmpv** playback.

---

### 🔊 Audio Playback

Cinecore supports:

- **PCM audio**
- **Bitstream audio**
- **Exclusive output**
- **Non-exclusive output**
- Audio-track selection
- Backend-dependent playback and output options

The PCM path has received additional Beta V1 work and is also used by the real-time audio-analysis system.

---

### 🧪 Audio / Video Synchronization & Track Timing

Beta V1 includes **experimental synchronization controls** intended to help with media whose audio does not line up correctly with video.

Current experimental functionality includes:

- **Automatic audio/video sync detection and correction**
- **Manual audio-track delay / offset adjustment**

These features are **not considered validated or production-ready**. At the current stage, no guarantee is made about how accurately, consistently or reliably automatic correction works across different files, codecs, renderers or playback paths. Manual delay controls are available for testing and correction, but their behavior still needs broader verification as well.

In other words, these controls are part of Beta V1 because they are implemented and being developed — **not because they are already guaranteed to work perfectly**.

---

### 🎵 Music Mode

Beta V1 includes a much broader music experience built around a global player rather than treating music as a secondary file type.

Current functionality includes:

- Global music player
- Music-library navigation
- Queue management
- Lyrics
- Synchronized lyrics
- Album and track metadata
- Real-time audio-analysis pages

The music UI and navigation are part of the main interface flows currently being tested and refined.

---

### 📊 Real-Time Audio Analysis

Audio Mode includes real-time visualizations and measurements such as:

- Waveform / oscilloscope
- Spectrum analyzer
- Loudness
- Audio levels
- Phase
- Channel balance
- Additional visualization modes

PCM graph updates have been verified after repeated navigation between pages, with displayed values coming from the real audio sampler rather than placeholder data.

---

### 🎤 Lyrics & Automatic Synchronization

Lyrics integration is functional and Beta V1 includes improved synchronization logic.

The synchronization system can still struggle with:

- Alternate versions of the same song
- Live recordings
- Repeated choruses
- Difficult vocal-detection cases
- Recordings whose structure differs from the reference lyrics

Automatic synchronization may use the Python backend documented in `LyricsSynchronizationTests/README.md`.

---

### 📱 Browser Remote Control

Cinecore includes a browser-based remote designed for use on the local network.

The remote can expose and control:

- Playback state
- Playback controls
- Queue
- Library access
- Audio-track selection
- Subtitle-track selection

Beta V1 testing has specifically covered state recovery after reopening, timeout behavior, state refresh after commands and rejection of stale responses.

---

### 🍿 Cinema Mode

Cinema Mode provides an automated pre-movie home-cinema sequence.

It currently includes:

- Pre-playback movie placeholder screen
- **Dolby Atmos** demo playback
- **DTS:X / DTS-HD MA** demo playback
- **THX** demo playback
- **WLED integration**
- Automatic room-light control
- Automatic transition from demos to the selected movie

---

### ⏯️ Resume, Queue & Playback Continuity

Cinecore can resume media from the previously stored playback position and integrates playback continuity with the wider library experience.

Beta V1 also includes editable queue handling and overlay-based queue interaction, making playback state more deeply integrated into the main UI than in the old Alpha.

---

### ⏭️ Skip Intro / Outro

TV-series playback includes **Skip Intro / Outro** functionality, with support for progressing automatically to the next episode.

---

### 🖼️ Photo Viewer

The integrated photo viewer is implemented and its main navigation flow is included in the current Beta V1 interface testing.

---

### 📡 DLNA & Network Media

DLNA and network-based media workflows are implemented, alongside support for local files, network paths and URLs.

As with other network-dependent functionality, final behavior can vary depending on the local network, devices and external services, so broader compatibility testing is still required.

---

### 📺 YouTube & Online Sources

YouTube integration is present as part of Cinecore's broader URL and online-media support.

Online functionality depends on connectivity and external services and should still be considered an area under active validation rather than production-certified functionality.

---

### ⚙️ Settings & Localization

Cinecore includes integrated settings for the player and its connected services, including:

- Renderer and playback-related options
- API fields and external-service configuration
- Theme controls
- Interface preferences
- Italian and English localization

---

## 🌍 Localization

Cinecore Player currently supports two interface languages:

- 🇬🇧 **English**
- 🇮🇹 **Italian**

The interface language can be changed directly from the application settings.

---

## 🖥️ System Requirements

### End Users

- **Operating System:** Windows x64
- **Runtime:** .NET 9 Desktop Runtime
- **Web functionality:** WebView2
- **Supported / integrated video renderers:**
  - madVR
  - MPC Video Renderer (MPCVR)
  - EVR
  - libmpv playback path

Some advanced functionality depends on the selected renderer, installed filters, GPU, drivers, display and system configuration.

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

Keep the **entire output directory**. The executable alone does not contain all native DLLs, filters, resources and helper tools required by the application.

Optional backends must be available in the expected `third-parties` locations or installed on the system.

---

## ⚠️ Beta V1 Limitations

Beta V1 should be treated as an **advanced development build**, not as finished production software.

Despite the enormous jump over the old Alpha, current limitations include:

- No guarantee of identical behavior across all GPUs, drivers and display chains
- Renderer-specific differences between madVR, MPCVR, EVR and libmpv
- madVR, HDR and refresh-rate switching still requiring broader configuration testing
- Remaining performance and responsiveness optimization
- UI details that still need refinement and consistency work
- Synchronized-lyrics edge cases
- Experimental automatic audio/video sync detection and correction is not yet broadly validated
- Manual audio-track delay / offset controls still require wider backend and media testing
- Dependence on external providers for metadata and online functionality
- Network- and device-specific DLNA behavior
- Additional stability testing still required for long sessions and unusual media combinations
- General bug fixing and quality-of-life work still in progress

In short: **Beta V1 is dramatically more complete than the old Alpha, but it is not yet optimized, perfect or production-ready.**

---

## 🛠️ Ongoing Development

Current work is focused on turning the already feature-rich Beta V1 into a more consistent and robust release.

Main areas include:

- Playback performance and stability
- PCM audio improvements
- Renderer-specific compatibility work
- madVR behavior after pause/resume and other playback transitions
- HDR handling and analysis
- Refresh-rate switching reliability
- Expanded renderer settings
- HUD and overlay refinement
- UI responsiveness and polish
- Synchronized-lyrics accuracy
- Metadata and online-service reliability
- DLNA metadata and playback consistency
- Additional media-center functionality
- Quality-of-life improvements
- Automatic audio/video sync detection and correction accuracy and reliability
- Manual audio-track delay / offset validation
- Bug fixes and regression testing

Future or experimental work may also include features such as **360° video rendering**, further HUD personalization and additional analysis tools.

---

## 🚧 Beta V1

**Cinecore Player Beta V1** is the current development generation of Cinecore.

Compared with the old Alpha, it represents a major step forward across virtually every visible part of the application: the interface has been heavily reworked, the media library is broader, music has become a first-class mode, playback controls are deeper, audio analysis is substantially more capable, remote-control behavior is more robust and many workflows that were previously basic or incomplete are now integrated into a coherent media-center experience.

### Included in Beta V1

- Redesigned application interface
- Redesigned HUD / playback overlays
- TMDb-integrated movie and TV library
- Search, scanning, artwork, details and cast metadata
- Favorites and viewing diary/history
- Resume playback
- Playlist and editable queue system
- Local files, network paths and media URLs
- YouTube integration
- DirectShow and libmpv playback paths
- madVR, MPCVR and EVR integration
- Audio- and subtitle-track controls
- **Experimental automatic audio/video sync detection and correction**
- **Experimental manual audio-track delay / offset adjustment**
- Forced-subtitle language handling
- HDR, bitstream and backend-dependent upscaling controls
- Five-preview seek timeline
- Picture-in-picture
- Global music player
- Lyrics and synchronized-lyrics workflow
- Waveform, spectrum, loudness, levels, phase and balance analysis
- Browser remote control for playback, queue, library and track selection
- Cinema Mode with WLED and demo automation
- Photo viewer
- Skip Intro / Outro and next-episode behavior
- Integrated settings, API configuration and themes
- Italian and English UI

### What Beta V1 is not yet

Beta V1 is **not a production-ready release** and should not be presented as one. The current build still requires optimization, compatibility testing, UI polish and broader real-world validation. Some systems — including automatic A/V synchronization and audio-track timing controls — are explicitly experimental and may behave inconsistently or incorrectly depending on the media and playback backend.

The key distinction is that the remaining work is now happening on top of a product that is **far more complete than the old Alpha**. Beta V1 is not merely the old Alpha with a few fixes: it is a substantially expanded version of Cinecore with many new systems already implemented and under active testing.

---

## 🧪 Verification & Bug Reports

The Beta V1 codebase includes deterministic lyrics tests, WinForms interface checks, decoder tests and remote-control tests.

When reporting a playback issue, please include:

- Renderer
- Media format
- Resolution
- Windows display scaling
- GPU
- Exact steps required to reproduce the issue

Only include relevant log excerpts and remove personal paths, tokens and credentials before posting them.

For deeper technical details, see the Beta V1 verification report when available in the repository:

[`artifacts/september27/README.md`](artifacts/september27/README.md)

Release notes:

[`RELEASE_NOTES_BETA_V1.md`](RELEASE_NOTES_BETA_V1.md)

---

## 💡 Suggestions & Feedback

Feedback is especially useful during the Beta V1 development phase.

If you find a bug, have an idea for a feature or want to suggest an improvement, feel free to open an **Issue** on GitHub.

---

## 📦 Availability

The old Alpha has been **retired** and is no longer presented as the current downloadable release.

**Beta V1 is the active development version**, but it should still be treated as experimental software rather than a production-ready release. If you build or test the current codebase, expect unfinished optimization, renderer-specific issues and features whose reliability is still being evaluated.

---

## 📄 License

Cinecore Player is distributed under the **PolyForm Noncommercial 1.0.0 License**.

The project is source-available and may be used, modified and studied under the conditions defined by the license.

Commercial use is not permitted.

For more information, see the [`LICENSE`](LICENSE) file included in the repository.

---

## ⭐ Support the Project

If you like Cinecore Player, consider leaving a **⭐ Star** on the repository.

It helps the project grow and makes it easier for other users to discover it.

---

**Cinecore Player**  
*A modern Windows media player focused on playback quality, customization and the home-cinema experience.*
