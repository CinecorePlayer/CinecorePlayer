<h1 align="center">🎬 Cinecore Player</h1>

<p align="center"><img src="Screenshots/Screenshot%202026-09-28%20124315.png" alt="Cinecore Player Spotlight" width="100%"></p>

<div align="center">

**A home-cinema media player for Windows**<br>
madVR · MPC Video Renderer · mpv · Blu-ray · HDR analysis · Jellyfin

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)](#-download--requirements)
[![Version](https://img.shields.io/badge/version-Beta%203.5%20(0.3.5)-yellow)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Status](https://img.shields.io/badge/status-beta-orange)](#-status)
[![Downloads](https://img.shields.io/github/downloads-pre/CinecorePlayer/CinecorePlayer/total.svg)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/CinecorePlayer/CinecorePlayer?style=flat&logo=github)](https://github.com/CinecorePlayer/CinecorePlayer)
[![Languages](https://img.shields.io/badge/languages-English%20%7C%20Italian-4C9EEB)](#-features)

[**📥 Download**](#-download--requirements) · [**✨ Features**](#-features) · [**📸 Screenshots**](#-screenshots) · [**🚧 Status**](#-status) · [**⚠️ Limitations**](#%EF%B8%8F-known-limitations) · [**🛠️ Build**](#%EF%B8%8F-building-from-source)

</div>

<br>

<table>
<tr>
<td width="33%"><img src="Screenshots/Screenshot%202026-09-28%20105926.png" alt="Film library"></td>
<td width="33%"><img src="Screenshots/Screenshot%202026-09-28%20110447.png" alt="Movie details"></td>
<td width="33%"><img src="Screenshots/Screenshot%202026-09-28%20110024.png" alt="Music player"></td>
</tr>
</table>

Cinecore Player is a **free**, **source-available** and **non-commercial** media player built in **C# / .NET 9.0**, for people who care about picture, sound and the home-cinema experience.

---

## 🎯 Highlights

<table>
<tr>
<td width="33%" valign="top">

### 🎥 Reference playback
madVR, MPC Video Renderer, EVR and mpv, with HDR, 3D, bitstream and automatic refresh-rate switching.

</td>
<td width="33%" valign="top">

### 💿 Blu-ray, DVD & ISO
Folders, discs and ISO files. Protected discs through MakeMKV, or libaacs with your own `KEYDB.cfg`.

</td>
<td width="33%" valign="top">

### 🖼️ Constant-height image
Constant height / area in fullscreen, black-bar detection and IMAX scenes.

</td>
</tr>
<tr>
<td valign="top">

### 🌈 HDR analysis
What the file declares vs. what it contains: whole film and real time, with waveform, CIE diagram and export.

</td>
<td valign="top">

### 💬 Smart subtitles
Search, download and automatic re-timing on the film's audio.

</td>
<td valign="top">

### 🎚️ Own audio engine
EQ, ReplayGain, crossfeed, crossfade, exclusive WASAPI and VU meters for music.

</td>
</tr>
<tr>
<td valign="top">

### 📡 Jellyfin & Trakt
Native Jellyfin client with selectable quality, DLNA / Plex browsing and Trakt recommendations.

</td>
<td valign="top">

### 🍿 Cinema Mode
Pre-film screen, WLED room lights and HTTP / MQTT home automations.

</td>
<td valign="top">

### 📱 Phone remote
Pair with a QR code and control playback, library and Spotlight from the browser.

</td>
</tr>
</table>

---

## 📥 Download & Requirements

**[Download `CinecorePlayer-Beta3.5-Setup.exe`](https://github.com/CinecorePlayer/CinecorePlayer/releases)** and run it. Nothing else has to be installed first: the installer includes the .NET runtime, FFmpeg, libmpv, yt-dlp and libaacs / libbdplus, and lets you pick the optional components (LAV Filters, MPC Audio Decoder / Renderer, madVR, MPC Video Renderer, XySubFilter, WebView2).

- **System:** Windows 10 (1809 or later) or Windows 11, x64. Rounded corners and Segoe UI Variable need Windows 11.
- **Installer:** English or Italian, follows the Windows theme, installs over a previous version and registers Cinecore in "Installed apps". Administrator approval is needed for Program Files and for registering the filters. Uninstalling leaves your data in `%APPDATA%\CinecorePlayer2025`.
- **Updates:** from Beta 3 on, an installed copy updates itself (checksum-verified). Beta 1 and 2 need one manual install.

> [!NOTE]
> The releases `beta-v0.3.3` and `beta-v0.3.4` were built with the project still numbered 0.3.2, so copies installed from them keep offering 0.3.4. Installing 0.3.5 fixes the numbering.

---

## 🚧 Status

**Beta 3.5 (0.3.5)**, a patch on top of Beta 3. The app is clean and runs without problems in everyday use, and everything listed here works. It is still a beta: it needs more testing on a wider range of GPUs, renderers, displays, audio devices and networks to find remaining bugs and imperfections, and parts of the interface may change in later versions.

---

## ✨ Features

Click a section to expand it.

### 🎥 Video

<details>
<summary><b>🎥 Playback & renderers</b></summary>

<br>

- **DirectShow** with madVR, MPC Video Renderer or EVR, or **libmpv**; local files, network paths, URLs and YouTube (yt-dlp + FFmpeg)
- HDR, 3D (including 3D-to-2D), upscaling, bitstream output and automatic refresh-rate switching
- **Volume above 100%** with a limiter, and per-film volume memory
- Seek timeline with five larger previews, also for network sources
- Picture-in-picture, editable queue, Skip Intro / Outro, optional fullscreen start
- **Info panel** with live bit rate and dropped frames, plus what LAV, madVR, MPC VR and mpv report (decoder, GPU, refresh rate, colour matrix, jitter, network buffer)
- **Native settings pages** for madVR, LAV Video / Audio, MPC Video Renderer and XySubFilter; the original panels stay available

<table>
<tr>
<td width="50%"><img src="Screenshots/settings-madvr.png" alt="madVR settings page"></td>
<td width="50%"><img src="Screenshots/settings-mpcvr.png" alt="MPC Video Renderer settings page"></td>
</tr>
</table>

</details>

<details>
<summary><b>💿 Blu-ray, DVD & ISO</b></summary>

<br>

- Open `BDMV` / `VIDEO_TS` folders, discs and ISO files from *Open disc*, *Open file*, the library and "Open with"
- **Blu-ray** works with madVR, MPC VR, EVR and mpv; **DVD** with mpv. The main title plays, there is no disc menu
- **ISO** files are mounted read-only by Windows (no administrator rights), with mpv as fallback
- **Protected discs** (AACS / BD+), no keys included: either **MakeMKV** installed (preferred, required for UHD), or bundled **libaacs + your own `KEYDB.cfg`** imported from *Settings › Playback › Protected Blu-rays*
- Clear message instead of a black screen when a disc cannot be decrypted; malformed discs are rejected safely

</details>

<details>
<summary><b>🖼️ Fullscreen image: constant height / area</b></summary>

<br>

Off by default. *Settings › General › Fullscreen image*, or the **Z** key during playback.

- Modes: **Fill**, **Constant height**, **Constant area**, **Custom** (height ↔ area slider and a relative height per aspect ratio, from 1.33 to 2.40)
- Never distorted, never off screen; **black bars inside the frame** are detected without flicker; **IMAX scenes** can use the whole screen
- Adjustable transition (none to 1200 ms); subtitles and OSD follow the picture
- Fullscreen only: windowed, PiP, 3D and the pre-film demo stay on "Fill"

</details>

<details>
<summary><b>🌈 HDR analysis</b></summary>

<br>

- **Declared vs. measured**: MaxCLL, MaxFALL, mastering display, Dolby Vision profile and HDR10+ next to the real peak and average luminance through the whole film (PQ scale, click to jump)
- Luminance distribution, gamut use (Rec.709 / P3 / BT.2020), percentiles, black level, dynamic range in stops and active picture area, with black bars excluded
- **Real-time view**: RGB waveform, level bars, CIE 1931 diagram, false colour, last-minute history and vectorscope, measured frame by frame
- **Export** as HTML report, PNG, CSV or JSON
- Own decoder: independent of the renderer, no impact on playback; results cached per file

<img src="Screenshots/hdr-analyzer.png" alt="HDR Analyzer" width="100%">

</details>

<details>
<summary><b>💬 Subtitles</b></summary>

<br>

- Track selection with an explicit **Off**, forced tracks by language, custom position, height on screen, subtitles in the black bars
- **Search and download**: YIFY Subtitles (films), Gestdown / Addic7ed (series), OpenSubtitles with your own key
- **Automatic re-timing** on the film's audio, also stretching between 25 and 23.976 fps; existing `.srt` files can be re-timed too, keeping a copy of the original
- Saved as `Film.it.srt` next to the video, or in Cinecore's folder when the video folder is read-only
- Only titles and public IDs leave your PC, never file names or paths

</details>

### 🔊 Audio

<details>
<summary><b>🧪 Audio / video synchronization</b></summary>

<br>

- Automatic alignment of an external or second audio track over many scenes, with detection of different speeds (24 / 23.976 / 25 fps)
- **Delay curve** for tracks whose offset changes during the film (up to 72 points, changes located within about 8 seconds); the player follows it by itself
- Manual delay with live preview; nothing is saved before Apply
- Experimental: check the result before relying on it

<img src="Screenshots/audio-sync.png" alt="Audio synchronization" width="50%">

</details>

<details>
<summary><b>🔊 Receiver control</b></summary>

<br>

Volume and mute for Denon / Marantz, Onkyo / Integra / Pioneer (eISCP), Yamaha MusicCast and UPnP / DLNA RenderingControl. The device is remembered and checked at startup, and found by network search.

**Safe start**: never above the last level used, at most +3 dB per command, no increase when already above −20 dB. The slider follows your gesture, so it stays correct even if the receiver was moved with its own remote.

</details>

<details>
<summary><b>🎚️ Music: Cinecore Audio Engine</b></summary>

<br>

- FFmpeg decoding, soxr resampling, double-precision DSP, **shared or exclusive WASAPI**; can be switched off to use mpv
- Equalizer (10-band, 31-band or parametric), ReplayGain, crossfeed, stereo width, loudness compensation, true-peak limiter
- **Crossfade** (3 / 6 / 10 s) and **Radio** (similar tracks from your library when the queue ends); output follows the Windows default device
- **Lyrics** from LRCLIB, lyrics.ovh and Genius, with automatic synchronization
- Artist photos and covers from **TIDAL** and **Spotify**
- **Real-time analysis**: waveform, spectrum, analog-style VU meters, true peak, LUFS, dynamics, phase correlation, exportable

<table>
<tr>
<td width="33%"><img src="Screenshots/audio-engine.png" alt="Audio engine settings"></td>
<td width="33%"><img src="Screenshots/equalizer.png" alt="Parametric equalizer"></td>
<td width="33%"><img src="Screenshots/vu-meters.png" alt="VU meters"></td>
</tr>
</table>

</details>

### 📚 Library & network

<details>
<summary><b>🎞️ Media library & Spotlight</b></summary>

<br>

- Movies, series, videos, music and photos; search by title, series or cast
- **TMDb** metadata and artwork; **IMDb, Letterboxd and Metacritic** ratings and reviews
- **Spotlight** discovery surface with a Library / Network toggle
- Favorites, viewing diary, resume and "Continue watching" (removable with a right-click)
- **Fix title and cover**: pick the right film among the TMDb results and one of its covers, or use your own image
- **Playlists** that mix films, series, music and photos; editable queue
- **Library report**: duplicates, low resolution or bit rate, inconsistent HDR metadata, unreadable files

<table>
<tr>
<td width="50%"><img src="Screenshots/reviews.png" alt="Ratings and reviews"></td>
<td width="50%"><img src="Screenshots/playlist-sheet.png" alt="New playlist sheet"></td>
</tr>
</table>

</details>

<details>
<summary><b>📡 Jellyfin, DLNA & Trakt</b></summary>

<br>

- **Jellyfin**, native: automatic discovery, password or **Quick Connect** sign-in, the server's own metadata and artwork
- Original file played directly with your renderer; **selectable quality** converts on the server when you want a lower bit rate (right-click menu, settings or remote)
- Progress, resume points and "played" stay in sync with your other devices
- **DLNA / UPnP** servers (Plex and others) browsable and playable; Jellyfin and Plex are listed first, with favourites and automatic address follow-up
- **Trakt**: link with a code, personal recommendations, related titles in the detail sheet, optional history sync
- HDR analysis, subtitle download and audio alignment need local files

</details>

<details>
<summary><b>🖼️ Photos</b></summary>

<br>

- Slideshow, zoom, pan and keyboard navigation
- Toolbar: edit, delete (Recycle Bin), share, open with, print, copy, show in folder
- **Editor**: pen, highlighter, line, arrow, rectangle, ellipse, text, eyedropper, colour picker, nine filters and rotation. It always saves a copy

<img src="Screenshots/photo-viewer.png" alt="Photo viewer" width="60%">

</details>

### 🏠 Home cinema

<details>
<summary><b>🍿 Cinema Mode & automations</b></summary>

<br>

- **Pre-film screen** with the film's artwork, optional demo clip and automatic transition to the film
- **WLED** room lights
- **Automations** (HTTP or MQTT) on start, pause, resume, stop and end credits: Home Assistant, Shelly, Hue bridge and anything with a URL. Rules can be limited to video and tested from the sheet

</details>

<details>
<summary><b>📱 Phone remote</b></summary>

<br>

- Browser-based remote for phones and tablets on the local network: playback, queue, library (Computer / Network), audio and subtitle tracks, Spotlight with D-pad, renderer and audio settings
- **QR pairing**: the interface dims and only the code remains; saved to the Home screen, the remote gets its own icon

</details>

### ⚙️ System

<details>
<summary><b>🔄 Updates, security & settings</b></summary>

<br>

- **Player updates**: checked once a day, only when nothing is playing; size and SHA-256 verified against the values published by GitHub
- **External components**: yt-dlp updates itself; LAV Filters and MakeMKV on request, with SHA-256 verification. Installers never start by themselves. madVR, MPC VR, XySubFilter, libmpv and FFmpeg update with the player
- **Security**: keys, tokens and passwords encrypted with Windows DPAPI; remote pairing throttles wrong PINs; yt-dlp input cannot inject options
- **Settings**: TMDb, Spotify and TIDAL credentials, light / dark mode, accent colour, **English and Italian** (follows Windows on first start)

</details>

---

## 📸 Screenshots

*Taken on Beta 1: the layout is the same today, fonts, corners and some details have changed.*

<table>
<tr>
<td width="50%"><img src="Screenshots/Screenshot%202026-09-28%20105605.png" alt="Home"><br><sub><b>Home</b></sub></td>
<td width="50%"><img src="Screenshots/Screenshot%202026-09-28%20110338.png" alt="Viewing diary"><br><sub><b>Viewing diary</b></sub></td>
</tr>
<tr>
<td><img src="Screenshots/Screenshot%202026-09-28%20105955.png" alt="Music home"><br><sub><b>Music</b></sub></td>
<td><img src="Screenshots/Screenshot%202026-09-28%20110106.png" alt="Real-time audio analysis"><br><sub><b>Real-time audio analysis</b></sub></td>
</tr>
<tr>
<td><img src="Screenshots/Screenshot%202026-09-28%20110202.png" alt="Lyrics"><br><sub><b>Synchronized lyrics</b></sub></td>
<td><img src="Screenshots/Screenshot%202026-09-28%20110402.png" alt="DLNA servers"><br><sub><b>DLNA servers</b></sub></td>
</tr>
<tr>
<td><img src="Screenshots/Screenshot%202026-09-28%20110712.png" alt="Settings"><br><sub><b>Settings</b></sub></td>
<td align="center"><img src="Screenshots/WhatsApp%20Image%202026-09-28%20at%2011.11.36.jpeg" alt="Browser remote" height="300"><br><sub><b>Browser remote</b></sub></td>
</tr>
</table>

---

## ⚠️ Known Limitations

<details>
<summary>Click to expand</summary>

<br>

- **Discs:** decryption of protected Blu-rays and DVD playback have never been tested on a real disc (unprotected Blu-ray folders and ISO files are tested). No disc menu, no timeline previews; UHD needs MakeMKV
- **Video:** behaviour can differ between madVR, MPCVR, EVR and mpv; HDR and refresh-rate switching need more testing. Constant height does not enlarge beyond the screen with madVR / MPC VR / EVR (mpv does); black-bar detection works on local files only; the EVR scale has not been measured on screen
- **HDR analysis:** an estimate from sampled key frames, so a peak lasting a few frames can be missed; the real-time view needs a second decode alongside playback; Dolby Vision profile 5 cannot be measured
- **Audio:** the Cinecore Audio Engine is relatively new and exclusive output depends on the device and driver; crossfade needs shared output; lyrics can lose alignment on difficult tracks; automatic audio alignment is experimental; receiver control is verified on a Marantz, other devices depend on the protocol
- **Network:** Jellyfin tested with 10.11, network music plays through mpv, photos and live TV are not listed; subtitle sources are third-party sites and can change; ratings, metadata and lyrics depend on external providers; DLNA depends on the server
- **Other:** the Windows share panel only appears while the player is in the foreground; automatic updates work only for installed copies; some bundled third-party installers are unsigned; long sessions and unusual media still need more testing

</details>

---

## 🕑 Version History

<details>
<summary>Click to expand</summary>

<br>

**Beta 3.5 (0.3.5), patch.** Blu-ray / DVD / ISO · external components update · constant height / area · photo editor · new audio and subtitle aligners with delay curve · safe receiver volume · volume above 100% · TIDAL · selectable Jellyfin quality · QR pairing · fix title and cover · redesigned Info panel and dialogs · many fixes to HUD, remote, Spotlight and receiver volume.

**Beta 3 (0.3.0).** Automatic updates · native Jellyfin client · subtitle search and re-timing · HDR analysis · Trakt · home automations · crossfade and radio · library report · Network page with favourites · encrypted keys and tokens.

**Beta 2.** Cinecore Audio Engine · VU meters · native renderer settings pages · 3D · IMDb / Letterboxd / Metacritic · multi-scene audio alignment · playlist sheet · Genius lyrics · photo viewer · Inno Setup installer.

**Beta 1.** TMDb library · Spotlight · diary and resume · DirectShow and mpv playback · YouTube and DLNA · HDR and bitstream · seek previews · music player and lyrics · browser remote · Cinema Mode with WLED.

Full notes for each version are on the [Releases page](https://github.com/CinecorePlayer/CinecorePlayer/releases).

</details>

---

## 🛠️ Building from Source

<details>
<summary>Click to expand</summary>

<br>

Requires the **.NET 9 SDK**:

```powershell
dotnet restore CinecorePlayer2025.csproj
dotnet build CinecorePlayer2025.csproj -c Release
```

Run `bin/Release/net9.0-windows10.0.19041.0/CinecorePlayer2025.exe`. Keep the **entire output directory**: the executable alone does not contain the native DLLs, filters and helper tools. Optional backends must be in the expected `third-parties` locations or installed on the system.

To build the installer, install **Inno Setup 6.6 or later**, set `<Version>` and `<InformationalVersion>` in `CinecorePlayer2025.csproj` **before** building, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

The installer and `SHA256SUMS.txt` go to `artifacts\installer`, and the script prints the release tag to use.

</details>

---

## 🧪 Bug Reports & Feedback

The code is checked with deterministic lyrics-sync tests, a UI test bench (`artifacts/ui-qa`), measured DSP and decoder checks and playback regression checks, and the main features are verified on real files and hardware. Reports from other setups are what helps most. Open an **Issue** and include:

Windows version and display scaling · GPU · renderer · audio output mode and device · media format and resolution · exact steps to reproduce.

Remove personal paths, tokens and credentials from any log excerpt before posting it.

---

## 📄 License

Original code under the **PolyForm Noncommercial 1.0.0 License**: it may be used, modified and studied, and commercial use is not permitted. See [`LICENSE`](LICENSE).

Third-party software keeps its own licenses. FFmpeg, libmpv and MPC Video Renderer are GPL-licensed; libaacs and libbdplus (bundled MSYS2 builds) are LGPL-licensed. See [`THIRD-PARTY-CREDITS.md`](THIRD-PARTY-CREDITS.md) and the notices under `third-parties`.

---

<div align="center">

If you like Cinecore Player, consider leaving a **⭐ Star**: it helps other users find the project.

*A modern Windows media player focused on playback quality, customization and the home-cinema experience.*

</div>
