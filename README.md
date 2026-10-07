# 🎬 Cinecore Player 2026

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements)
[![Version](https://img.shields.io/badge/version-Beta%203.5%20(0.3.5)-yellow)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Status](https://img.shields.io/badge/status-beta-orange)](#-status)
[![Downloads](https://img.shields.io/github/downloads-pre/CinecorePlayer/CinecorePlayer/total.svg)](https://github.com/CinecorePlayer/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/CinecorePlayer/CinecorePlayer?style=flat&logo=github)](https://github.com/CinecorePlayer/CinecorePlayer)
[![Languages](https://img.shields.io/badge/languages-English%20%7C%20Italian-4C9EEB)](#-localization)

Cinecore Player is a **free**, **source-available** and **non-commercial** media player for Windows, built in **C# / .NET 9.0** and focused on high-quality local playback, home-cinema use and a modern media-center experience.

It combines a DirectShow playback path with **libmpv**, **madVR**, **MPC Video Renderer (MPCVR)** and **EVR**, with Blu-ray / DVD / ISO playback, HDR handling and analysis, a TMDb-powered library, a native **Jellyfin** client, its own **audio engine for music**, real-time audio analysis, subtitle download and re-timing, a phone remote, home automation hooks and Cinema Mode.

---

## 🚧 Status

**Current version: Beta 3.5 (0.3.5)**, a patch update to Beta 3.

The application is in good shape: it is clean and runs without problems in everyday use, and the features described below are working. It is still a **beta**, though. It needs more testing on a wider range of GPUs, renderers, displays, audio devices and network setups to find remaining bugs and imperfections, and some parts of the interface may change in later versions.

What is and is not verified on real hardware is listed under [Known Limitations](#%EF%B8%8F-known-limitations). If you find a problem, please read [Verification & Bug Reports](#-verification--bug-reports).

---

## 📥 Download

Download the latest installer (**`CinecorePlayer-Beta3.5-Setup.exe`**) from the [Releases page](https://github.com/CinecorePlayer/CinecorePlayer/releases) and run it.

The installer:

- runs in English or Italian and follows the Windows light or dark theme;
- includes Cinecore, the .NET runtime, FFmpeg, libmpv, yt-dlp, and libaacs / libbdplus for Blu-ray, so nothing else has to be installed first;
- lets you select the optional third-party components one by one: LAV Filters, MPC Audio Decoder, MPC Audio Renderer, madVR, MPC Video Renderer, XySubFilter and, only if it is missing, the WebView2 Runtime (downloaded from Microsoft);
- installs over a previous version, closing Cinecore if it is running;
- registers Cinecore in "Installed apps"; uninstalling also unregisters the filters and leaves your data in `%APPDATA%\CinecorePlayer2025`.

Administrator approval is required for the default Program Files location and for registering the filters. Some bundled third-party installers are unsigned and open with their own setup windows.

From Beta 3 on, an installed copy **updates itself** (see [Updates](#-updates)). Beta 1 and Beta 2 do not have this: install a newer version once by hand.

> [!NOTE]
> The releases `beta-v0.3.3` and `beta-v0.3.4` were built with the project still numbered 0.3.2, so a copy installed from them believes it is 0.3.2 and keeps offering 0.3.4. Installing 0.3.5 makes the numbering consistent again.

## 🖥️ System Requirements

- **Operating system:** Windows 10 (version 1809 or later) or Windows 11, x64. Rounded window corners and the Segoe UI Variable typeface require Windows 11.
- **Runtime:** none to install; the installer includes the .NET runtime.
- **WebView2 Runtime:** needed for WebView-backed pages; the installer offers it when it is missing.
- **Video renderers:** libmpv is bundled. madVR, MPC Video Renderer and LAV Filters are optional components offered by the installer. EVR is part of Windows.
- **Protected Blu-rays:** MakeMKV (optional, not bundled) or your own `KEYDB.cfg`. See [Discs](#-discs-blu-ray-dvd--iso).

Advanced video features depend on the selected renderer, the installed filters, the GPU drivers and the display.

---

## ✨ Features

### 🎥 Video Playback

- **DirectShow** playback with **madVR**, **MPC Video Renderer (MPCVR)** or **EVR**, and **libmpv** playback
- Local files, network paths, media URLs and YouTube (through the bundled yt-dlp and FFmpeg)
- Audio-track and subtitle-track selection, with an explicit **Off** state for subtitles
- HDR, 3D and upscaling options where the active renderer and hardware support them
- **3D playback** through madVR and MPC Video Renderer, including 3D-to-2D, swap eyes and output format
- Bitstream output and PCM output
- Automatic refresh-rate switching
- **Volume above 100%** with a limiter, and a **per-film volume memory**
- Seek timeline with five previews: larger frames, the frame under the pointer highlighted and a mark on the bar. Previews also work for network sources (one frame every ten seconds)
- Picture-in-picture mode
- Editable playback queue, also from the playback overlay
- **Skip Intro / Outro** and next-episode button for TV series
- Start in fullscreen (optional)
- Menu entries that cannot be used at the moment are disabled instead of failing when clicked

**Native settings pages.** madVR, LAV Video, LAV Audio, MPC Video Renderer and XySubFilter have settings pages inside Cinecore, in the same style as the player's own settings. madVR options such as chroma upscaling, NGU, image doubling, dithering, HDR handling and smooth motion can be changed without opening the madVR panel. The original panels remain available for everything else.

![madVR settings page](Screenshots/settings-madvr.png)

![MPC Video Renderer settings page](Screenshots/settings-mpcvr.png)

**Playback info panel.** Three live figures (video and audio bit rate, dropped frames), then Video, Audio and System sections. Next to the values measured by the player it shows what the components themselves report: the video decoder in use and the graphics card it runs on (LAV Video), what the audio decoder receives and produces (LAV Audio), the audio device, renderer version, exclusive mode, real output rectangle, display refresh rate, colour matrix and levels (madVR), frames presented and dropped, jitter (madVR, MPC Video Renderer, EVR), and for mpv its version, outputs, hardware decoding and network buffer. Network streams (Jellyfin, DLNA, HTTP/HTTPS, YouTube) show their origin, container, size and bit rates too. The panel stays on screen until you close it.

### 💿 Discs: Blu-ray, DVD & ISO

- Open `BDMV` or `VIDEO_TS` folders, inserted discs and ISO files from the *Open disc* menu, from *Open file*, from the library and from "Open with"
- **Blu-ray** plays with madVR, MPC Video Renderer and EVR (through LAV Splitter) and with mpv; **DVD** plays with mpv
- **ISO** files are mounted by Windows as a read-only drive for the duration of the film (no administrator rights needed), so every renderer can read them; if mounting fails, playback falls back to mpv, which reads the ISO directly
- The main title plays; there is **no disc menu**
- **Protected Blu-rays (AACS / BD+)** have two paths, and no keys are distributed with the program:
  1. **MakeMKV installed**: Cinecore uses its library in place (this also covers BD+). This is the preferred path and the only one for UHD Blu-rays
  2. **libaacs + your own `KEYDB.cfg`**: libaacs and libbdplus are bundled; import the key file from *Settings › Playback › Protected Blu-rays*
- A disc that cannot be decrypted shows a clear message instead of a black screen, and a malformed disc is rejected before it can crash the player

MakeMKV is not bundled because it is updated often; Cinecore downloads it from the official site on request (see [Updates](#-updates)).

### 🖼️ Fullscreen Image: Constant Height / Constant Area

Controls how the picture fills the screen in fullscreen. **Off by default** ("Fill" is the classic behaviour). Settings › General › Fullscreen image, or the **Z** key during playback.

- Four modes: **Fill**, **Constant height**, **Constant area** and **Custom**
- Custom has a height ↔ area slider and a relative height for each aspect ratio (1.33, 1.43 IMAX, 1.78, 1.85, 2.00, 2.35 / 2.40)
- The picture is never distorted and never goes off screen
- **Black bars inside the frame** (2.39 films encoded in 16:9) are detected, and the format changes only after two matching measurements, so dark scenes and fades do not make it flicker
- **IMAX scenes** use the whole screen when the picture becomes taller than the film's prevailing format (can be switched off)
- Adjustable transition when the format changes: none, 200, 400, 800 or 1200 ms
- Subtitles and OSD follow the new rectangle
- Applies only in fullscreen; windowed mode, picture-in-picture, 3D, the pre-film demo and the pause placeholder stay on "Fill"

### 💬 Subtitles

- Track selection, forced-track selection based on language, and position options: custom position, height on screen, subtitles in the black bars
- **Search and download** for the film or episode being played: YIFY Subtitles for films, Gestdown (the Addic7ed catalogue) for series, and OpenSubtitles when you add your own key
- The downloaded subtitle is **re-timed on the film's audio** before it is saved: the dialogue is located using audio energy and the subtitle is shifted, and stretched when it was made for another frame rate (25 vs 23.976 fps)
- An existing `.srt` can be re-timed the same way; it is rewritten only when the analysis is reliable, and a copy of the original is kept
- Subtitles are saved next to the video as `Film.it.srt`, or in Cinecore's own folder when the video folder is read-only
- External subtitles load with madVR / XySubFilter and with mpv; the choice is remembered per film
- Only titles and public IDs are sent to the subtitle services, never file names or paths

### 🌈 HDR Analysis

A sheet for the video being played shows what the file declares (MaxCLL, MaxFALL, mastering display, Dolby Vision profile, HDR10+) next to what it actually contains:

- peak and average luminance through the whole film, measured on sampled frames, on the PQ scale; a click on the chart jumps to that point
- luminance distribution, and how much of the picture stays within Rec.709, uses P3 or reaches BT.2020
- the absolute maximum next to the 99.99th-percentile peak, median, 90th and 99th percentile, black level, mean luminance (Y), dynamic range in stops and the active picture area; black bars are detected and left out, so averages and black level describe the picture only
- a **Real time** view: the film is decoded frame by frame alongside playback and every frame is measured as it reaches the screen, with an **RGB waveform** on the PQ scale, peak and average **level bars** with the declared MaxCLL and the highest peak seen, a **CIE 1931 chromaticity diagram** with the Rec.709, P3 and BT.2020 triangles and the share of the picture inside each, a **false-colour** picture by luminance band, the **last minute** of peak and average, and a **vectorscope**
- on a computer that cannot decode in real time the view steps down by itself to reference frames, then to key frames
- **Export**: an HTML report with every figure and the charts of both views, a PNG of the current view, the samples as CSV or all measurements as JSON

The analysis uses its own decoder, so it does not depend on the renderer and does not disturb playback. Results are cached per file.

![HDR Analyzer](Screenshots/hdr-analyzer.png)

### 🧪 Audio / Video Synchronization

- **Automatic alignment** of an external or second audio track by correlation over many scenes
- Detection of tracks that run at a different speed (24 / 23.976 / 25 fps), with speed conversion
- **Delay curve** for tracks whose offset changes during the film: up to 72 refinement points, changes located within about 8 seconds, and the player follows the curve by itself during playback
- Manual delay with a live preview; nothing is saved before Apply

These controls are **experimental**. Automatic alignment is not guaranteed to be correct for every file, dub or edit; check the result before relying on it.

![Audio synchronization](Screenshots/audio-sync.png)

### 🔊 Audio & Receiver Control

- Output through DirectShow or libmpv, PCM or bitstream
- **Network receiver control** (volume and mute): Denon / Marantz, Onkyo / Integra / Pioneer (eISCP), Yamaha MusicCast and UPnP / DLNA RenderingControl
  - the saved device is remembered and checked on startup, and can be found by network search
  - **safe start**: never above the last level used, at most +3 dB per command, and no increase when the volume is already above −20 dB
  - the slider counts the gesture, not an absolute position, so it works even if the receiver was moved with its own remote, and it stops where the command can really arrive

### 🎵 Music

Music plays through the **Cinecore Audio Engine**: FFmpeg decoding, high-precision soxr resampling and a double-precision DSP chain, with shared or exclusive WASAPI output. In exclusive mode the device runs at the file's sample rate, bypassing the Windows mixer. The engine can be switched off to use mpv instead.

- Equalizer: 10-band graphic, 31-band third-octave graphic, or parametric (frequency, gain, Q and shape, including low and high shelf), edited directly on the graph
- ReplayGain per track or per album, with preamp
- Headphone crossfeed, stereo width, balance and mono sum
- Loudness compensation for low-volume listening
- Clip protection: true-peak limiter plus automatic headroom
- **Crossfade** between tracks (3, 6 or 10 seconds); consecutive tracks of the same album are still joined without overlap
- **Radio**: when the queue ends, playback continues with similar tracks from the library (same artist and collaborators, nearby years, favourites), without any online service
- Output with "PC default" follows the Windows default device when it changes
- Global music player, music-library navigation and queue management
- **Lyrics** from LRCLIB, lyrics.ovh and Genius, with automatic synchronization (see below)
- Album and track metadata; artist photos, covers and album backgrounds from **TIDAL** and **Spotify** (selectable source, keys stored encrypted)
- Translucent music bar with animated entrance and exit; the music bar, the queue and the right-click menu share one glass material

![Cinecore Audio Engine settings](Screenshots/audio-engine.png)

![Parametric equalizer](Screenshots/equalizer.png)

**Real-time audio analysis.** Waveform / oscilloscope, spectrum analyzer, analog-style VU meters with automatic calibration that follows the loudness of the track (or fixed reference levels), true peak, clipping and limiter readouts, loudness (LUFS), RMS level and dynamics, phase correlation and channel balance. The analysis data can be exported, and each chart explains what it shows.

![VU meters](Screenshots/vu-meters.png)

**Lyrics synchronization.** Lyrics without timings are aligned to the audio in the background. Synchronization can still be off for alternate versions, live recordings, repeated choruses and songs whose structure differs from the reference lyrics. The Python backend is described in `LyricsSynchronizationTests/README.md`.

### 🎞️ Media Library

Libraries for movies, TV series, videos, music and photos.

- Library scanning; search by title, series or cast, with an "All sources" filter
- **TMDb metadata**: artwork, details and cast. Accented titles are recognised, the English plot is used when the Italian one is missing, and Jellyfin data comes first when a server is connected
- **Ratings and reviews** from IMDb, Letterboxd and Metacritic, with spoiler warnings
- **Spotlight**: a separate discovery surface with large backdrops, year and runtime, video / HDR and audio-format badges, synopsis and cast, direct **Play** and **Details** actions and a carousel. A **Library / Network** toggle switches its source
- Favorites, viewing diary, resume playback and "Continue watching"; titles can be removed from "Continue watching" or from the diary with a right-click
- **Fix title and cover**: from the film's menu or by right-clicking the poster, correct the title and year, pick the right film among the TMDb results and one of its covers, or choose an image from your computer. "Restore" removes the correction
- **Playlists** with name, description and cover; a playlist can mix films, series, music and photos
- **Library report**: number of titles, space and formats, plus what deserves a look: duplicates, low resolution or bitrate, HDR files with missing or inconsistent luminance metadata, files that do not open
- Filters and sorting: one click moves to the next; list filters (director, decade, year, genre, folder) are on the right-click of the Filter button
- Home follows the source in use (Computer or Network), and the sidebar marks which one is active
- Detail and album sheets fade in and out; moving between pages cross-fades
- Empty libraries explain what to add; disconnected drives are shown as such
- "Play" in the detail sheet starts from the beginning; resuming is done from "Continue watching"

![Ratings and reviews](Screenshots/reviews.png)

![New playlist sheet](Screenshots/playlist-sheet.png)

### 📡 Jellyfin, DLNA & Network Media

**Jellyfin.** Cinecore talks to a Jellyfin server directly, without the DLNA plugin:

- servers on the local network are found automatically; a server elsewhere can be added by address
- sign in with user name and password, or with **Quick Connect**: the player shows a code to enter on a device that is already signed in, and no password is typed into the player
- movies, episodes, videos and music arrive with the server's own titles, overviews, genres, artwork and technical details (resolution, HDR / Dolby Vision, audio format)
- the original file is played directly with the renderer and audio path you already use (madVR, bitstream, Cinema Mode with its pre-film screen); **selectable quality** converts the stream on the fly on the server when you want a lower bit rate, from the right-click menu, the Player settings or the remote. Changing quality during a film reopens it at the same point
- start, progress, pause and stop are reported to the server, so resume points and "played" stay in sync with your other devices, and titles left half-way appear in "Continue watching"
- films watched from Jellyfin reach the Trakt history through the TMDb id declared by the server
- the access token is stored encrypted and is never written to the log, the history or the resume file; removing the server signs out and revokes it

**DLNA / UPnP** servers (Plex and others) can be browsed and played alongside local files, network paths and URLs. Behaviour varies with the network and the server.

**Network page.** It lists only servers that have a catalogue to browse (Jellyfin, Plex, other DLNA servers), with Jellyfin and Plex first. Servers can be starred as favourites and stay on top; a server that changes IP address keeps a single entry that follows it, and entries that have not answered for a week are removed.

Functions that need the file itself (HDR analysis, subtitle download and re-timing, automatic audio alignment, per-film volume) are available for files on the PC, not for network streams.

### 🎬 Trakt

- Connect a Trakt account with a code; no password is typed into the player
- "Recommended for you": personal recommendations, split between titles already in the library and titles to find
- The recommendations in a film's detail sheet put first the library titles Trakt considers related to it (no account needed)
- Optionally, every finished film is added to the Trakt history; only public title IDs, dates and ratings are sent

### 📱 Phone & Browser Remote

A browser-based remote for phones and tablets on the local network:

- playback state and controls, queue and library access (with a **Computer / Network** toggle for the library)
- audio-track and subtitle-track selection
- Spotlight navigation with D-pad, OK and Back
- audio engine, renderer and **Player** settings (Jellyfin quality and fullscreen-image options)
- **Pairing** with a large QR code or a PIN: the interface dims and only the code remains; on first launch the player offers to pair your phone. Saved to the phone's Home screen, the remote gets its own icon

### 🍿 Cinema Mode & Home Automations

- Pre-playback placeholder screen with the film's artwork, optional pre-film demo clip and automatic transition to the selected film
- **WLED** integration for room-light control, with its own settings sheet and a live connection check
- **Automations**: rules that send a command when playback **starts, pauses, resumes, stops** or reaches the **end credits**: an HTTP request (Home Assistant, Shelly, Hue bridge and anything with a URL) or an MQTT message. Rules can be limited to video, tested from the sheet, and use the title, position and duration in the message

### 🖼️ Photo Viewer

- Slideshow, wheel zoom, drag to pan, arrow-key navigation
- Toolbar actions: edit, delete (to the Recycle Bin, with confirmation), share (Windows share panel), open with, print, copy (file and image) and show in folder
- **Photo editor**: full-window editor with pen, highlighter, line, arrow, rectangle, ellipse, text, eyedropper, the Windows colour picker, six colours, three thicknesses, nine filters and rotation. Ctrl+Z undoes, Ctrl+S saves, Esc closes. It **always saves a copy** next to the original (or elsewhere, when the folder is read-only)

![Photo viewer](Screenshots/photo-viewer.png)

### 🔄 Updates

**Cinecore itself.** An installed copy checks the GitHub releases shortly after startup, at most once a day, and offers a newer version only when nothing is playing. The update sheet shows the release notes; "Update now" downloads the installer, checks its size and SHA-256 against the values published by GitHub, and runs it without the wizard. Windows asks for consent, the files are replaced and Cinecore restarts. A version can be skipped, and the check can be switched off. Development builds and portable copies never replace themselves.

**External components** (*Extra › External components…*, or *Settings › Updates › External components*):

| Component | How it updates | File verification |
|---|---|---|
| yt-dlp | by itself, once a day at startup | SHA-256 published by GitHub |
| LAV Filters | on request: downloads the original installer and runs it (Windows asks for consent) | SHA-256 published by GitHub |
| MakeMKV | on request, from the producer's site | SHA-256 from the producer's checksum file |

An installer never starts by itself, and never while something is playing. The MakeMKV setup has no digital signature: its checksum comes from the same site as the download, so it protects against a corrupted file, not against a compromised site. madVR, MPC Video Renderer, XySubFilter, libmpv and FFmpeg live in the program folder and are updated with the player.

### 🔒 Security

- API keys, tokens and passwords are stored encrypted (Windows DPAPI for user data); nothing is kept in clear text in the configuration or in the logs
- Remote: pairing does not reveal the PIN, wrong PINs are throttled, commands are accepted only from the remote page, oversized requests are refused
- URLs and searches passed to yt-dlp cannot inject options or commands
- Only titles, public IDs, dates and ratings are sent to external services

### ⚙️ Settings

- Player, renderer, audio and startup options (including fullscreen start)
- TMDb, Spotify and TIDAL credentials, stored encrypted, with a built-in key or a personal one
- Automatic updates and external components, with the startup check optional
- Light and dark mode, accent colour
- Italian and English interface

---

## 🌍 Localization

Cinecore Player supports two interface languages:

- 🇬🇧 **English**
- 🇮🇹 **Italian**

On first start the interface is Italian when Windows is in Italian and English otherwise. The language can be changed in the application settings.

---

## 📸 Screenshots

The screenshots below were taken on Beta 1. The layout of these screens is the same today; fonts, corners and some details have changed.

### 🏠 Home, Library & Discovery

#### Home

![Cinecore Player Home](Screenshots/Screenshot%202026-09-28%20105605.png)

#### Spotlight

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

## ⚠️ Known Limitations

Cinecore is a beta: it needs more testing across hardware and setups, and parts of the interface may change.

**Discs**

- Decryption of protected Blu-rays has never been tested on a real disc, and DVD playback has never been tested on a real DVD. Non-protected Blu-ray folders and ISO files are tested
- No disc menu, and no timeline previews for discs; UHD Blu-rays depend on MakeMKV

**Video**

- Playback behaviour can differ between madVR, MPCVR, EVR and libmpv
- HDR handling and refresh-rate switching need testing on more configurations
- Fullscreen image: with madVR / MPC Video Renderer / EVR the picture is not enlarged beyond the screen (with mpv it is); black-bar detection works on local files, not on discs and network streams; the scale with EVR has not been measured on screen
- Native renderer and filter pages cover the main options, not every setting of the original panels
- The whole-film HDR analysis is an estimate from sampled key frames: a peak lasting a few frames can be missed. The real-time view measures every frame only if the computer can decode the film a second time alongside playback; otherwise it follows reference or key frames. Frames are measured on a reduced grid (one pixel in four across on 4K). Dolby Vision profile 5 cannot be measured

**Audio**

- The Cinecore Audio Engine is relatively new; exclusive output depends on the audio device and driver
- Crossfade needs the Cinecore Audio Engine with shared output
- Synchronized lyrics can lose alignment on difficult tracks
- Automatic audio alignment is experimental
- Receiver control has been verified on a Marantz receiver; other brands and protocols depend on the device

**Network & services**

- Jellyfin: tested with Jellyfin 10.11; music from the network plays through mpv, not through the Cinecore Audio Engine; photos and live TV are not listed
- Subtitle sources are third-party sites and can change or stop working; automatic re-timing is skipped when the analysis is not reliable
- Ratings, reviews, metadata and lyrics depend on external providers and on the network
- DLNA behaviour depends on the server and the network

**Other**

- The Windows share panel for photos only appears while the player is in the foreground
- Automatic updates work only for installed copies
- Some bundled third-party installers are unsigned
- Long sessions and unusual media combinations need more stability testing

---

## 🕑 Version History

Release notes for each version are on the [Releases page](https://github.com/CinecorePlayer/CinecorePlayer/releases).

### Beta 3.5 (0.3.5) — patch

- Blu-ray, DVD and ISO playback, with MakeMKV or libaacs + `KEYDB.cfg` for protected discs
- External components update (yt-dlp, LAV Filters, MakeMKV)
- Fullscreen image: constant height / constant area
- Photo toolbar and full-window photo editor
- New audio and subtitle aligners, with a delay curve for audio
- Network receiver: safe start and gesture-based volume slider
- Volume above 100%, fullscreen start, TIDAL and Spotify for music artwork, selectable Jellyfin quality
- Phone pairing by QR code, Library / Network toggle in Spotlight and remote, new "Player" page in the remote
- Fix title and cover, remove from "Continue watching" / diary, redesigned Info panel, larger timeline previews, network timeline previews
- Consistent redesign of the dialogs, and many fixes to the HUD, dialogs, remote, Spotlight and amplifier volume

### Beta 3 (0.3.0)

- Automatic updates from the GitHub releases, with checksum verification
- Native Jellyfin client
- Subtitle search and download with automatic re-timing
- HDR analysis, whole-film and real-time
- Trakt integration
- Home automations over HTTP and MQTT, WLED settings sheet
- Crossfade and radio for music, playback follows the Windows default audio device
- Library report and per-film volume memory
- Animated detail sheets, one glass material for music bar, queue and menus
- Network page with Jellyfin and Plex first, favourite servers, automatic address follow-up
- Encrypted storage for keys and tokens; hardened remote pairing

### Beta 2

- Cinecore Audio Engine, VU meters and audio analysis
- Native settings pages for madVR, LAV Video, LAV Audio, MPC Video Renderer and XySubFilter
- 3D playback, subtitle position options
- Ratings and reviews from IMDb, Letterboxd and Metacritic
- Multi-scene audio alignment with speed-mismatch detection
- New playlist sheet, cast search, source filter, empty-library guidance
- Genius lyrics, smoother lyrics timing, reworked photo viewer
- Spotlight navigation and new settings pages in the remote
- New typography, rounded corners, smoother animations; startup language follows Windows
- New Inno Setup installer in English and Italian

### Beta 1

- TMDb-integrated movie and TV library, Spotlight, favourites, viewing diary, resume
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

---

## 🛠️ Building from Source

Building Cinecore requires the **.NET 9 SDK**:

```powershell
dotnet restore CinecorePlayer2025.csproj
dotnet build CinecorePlayer2025.csproj -c Release
```

Run:

```text
bin/Release/net9.0-windows10.0.19041.0/CinecorePlayer2025.exe
```

Keep the **entire output directory**: the executable alone does not contain the native DLLs, filters, resources and helper tools the application needs. Optional backends must be in the expected `third-parties` locations or installed on the system.

To build the installer, install **Inno Setup 6.6 or later**, set `<Version>` and `<InformationalVersion>` in `CinecorePlayer2025.csproj` **before** building, and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

The installer and `SHA256SUMS.txt` are written to `artifacts\installer`, and the script prints the release tag to use.

---

## 🧪 Verification & Bug Reports

The codebase is checked with deterministic lyrics-synchronization tests, a UI test bench (`artifacts/ui-qa`), measured DSP and decoder checks for the audio engine, and playback regression checks, and the main features are verified on real files and hardware. This does not replace testing on a wider range of setups, which is where reports help most.

When reporting a playback issue, please include:

- Windows version and display scaling
- GPU
- Renderer
- Audio output mode (shared, exclusive or bitstream) and device
- Media format and resolution
- Exact steps to reproduce the issue

Only include relevant log excerpts, and remove personal paths, tokens and credentials before posting them.

## 💡 Suggestions & Feedback

If you find a bug, have an idea for a feature or want to suggest an improvement, open an **Issue** on GitHub.

---

## 📄 License

Cinecore Player's original code is distributed under the **PolyForm Noncommercial 1.0.0 License**: it may be used, modified and studied under the conditions of the license, and commercial use is not permitted. See [`LICENSE`](LICENSE).

Third-party software keeps its own licenses. FFmpeg, libmpv and MPC Video Renderer are GPL-licensed; libaacs and libbdplus (bundled MSYS2 builds) are LGPL-licensed. See [`THIRD-PARTY-CREDITS.md`](THIRD-PARTY-CREDITS.md) and the notices under `third-parties`.

---

## ⭐ Support the Project

If you like Cinecore Player, consider leaving a **⭐ Star** on the repository. It helps other users discover the project.

---

**Cinecore Player**  
*A modern Windows media player focused on playback quality, customization and the home-cinema experience.*
