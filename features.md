# Cinecore Player: All Features

Reference versions: **Windows Beta 4.1 (0.4.1)** and **macOS Beta 1.1 (0.1.1)** (Apple Silicon Mac, macOS 13 or later), November 2026.

Unless a line says otherwise, a feature is on both platforms. **Windows:** and **macOS:** mark what exists on one only or works differently. Features added in Windows Beta 4.1 are marked **(4.1)**.

## 1. What it plays

- **Local video**: MKV, MP4, M2TS, TS, MOV, AVI, WMV, WebM and the other formats read by mpv and LAV Filters.
- **Local music**: MP3, FLAC, WAV, OGG, Opus, M4A, AAC, WMA and others (also DSF and APE on Windows, AIFF on macOS).
- **Photos**: full-window viewer with filmstrip or slideshow (section 11).
- **Playlists**: `.wpl` files open from Open › Open playlist (on macOS also from the Finder).
- **Discs**: Blu-ray and DVD from an optical drive, from a folder (`BDMV` or `VIDEO_TS`) and from an ISO image (mounted automatically on macOS). Protected Blu-rays open only if the user supplies the keys: **Windows:** with MakeMKV installed or an imported key file; **macOS:** with libaacs and the user's own `KEYDB.cfg`, importable from Settings › Playback › Protected Blu-rays. The player includes no keys.
- **DVDs like on a standalone player**: disc menus and special features with every engine, in the correct format (16:9 or 4:3 anamorphic). Navigate with the arrows, Enter and Back, the mouse or the phone remote; `M` returns to the disc menu. **macOS:** menus use libdvdnav, with menus, warnings and short titles played by mpv and the film by the chosen engine; CSS-protected DVDs (libdvdcss) are supported.
- **Direct DVD reading (macOS)**: as an alternative to menus, go straight to the main title and pick chapters, audio and subtitles from the player menu (Settings › Playback › DVD reading).
- **Disc resume**: reopening a partly watched disc offers *Resume* / *Start over* (on Windows as a full-screen page with the film's artwork, on macOS as *Resume the film* / *From the menu*).
- **Discs in the library**: the inserted disc appears among the movies or albums and under Devices while it stays in the drive.
- **Audio CDs**: open like an album, identified through MusicBrainz (titles, artist, year, cover; saved for next time), read losslessly and played by the Cinecore Audio Engine (gapless, lyrics, scrobbling; on Windows also exclusive/bit-perfect and EQ).
- **Network servers**: Jellyfin, Emby, DLNA/UPnP and Plex (section 9).
- **YouTube and other sites** via yt-dlp, with a resolution limit (Windows: 144p to 8K). Direct files (`.mp4`, `.m3u8`…) open as they are. **Windows:** a single continuous load, standard quality names, audio always at the best quality, streams checked before opening. **macOS:** the thumbnail shows while the address is being resolved.
- **Files from the system**: *Open with*, double-click and drag-and-drop, also when the app is already open; the file goes to the existing window. **(4.1, Windows)** Videos, music and photos open straight away, without the startup screen.

## 2. Video engines

- **Engines**: **madVR**, **MPC Video Renderer** (with LAV Filters and XySubFilter) and **mpv**, one default plus a per-session choice.
  - **Windows:** madVR and MPC-VR run on DirectShow; mpv can decode on NVIDIA graphics cards (NVDEC) **(4.1)**.
  - **macOS:** mpv is native (Vulkan on MoltenVK, VideoToolbox decoding, HDR on the Mac screen in EDR); madVR and MPC-VR run under Wine for ARM64. An **Auto** choice is also available.
- **macOS: downloadable madVR/MPC-VR engine.** On first launch the app downloads MetalSharp Wine, madVR, MPC Video Renderer, LAV Filters and XySubFilter from their official sites, checks the SHA-256 of every archive and registers the filters. Progress and *Cancel* in Settings › Updates. Without the engine everything plays with mpv.
- **Exact presentation (macOS)** with madVR and MPC-VR: every frame leaves at the right vertical sync, on the screen where the film is, also on external screens and 23.976 / 24 Hz projectors.
- **The engine never changes on its own.** On battery saver, or if madVR keeps dropping frames, the player warns and offers a lighter engine, resuming from the same point. The warning has *Don't show again* and does not appear when a preset is active. **(4.1)**
- **0 dropped frames at startup and after seeking** with all engines; madVR starts with its render queue full **(4.1)** (macOS: Components › madVR › Performance).
- **Renderer presets (4.1)** in the right-click menu, *Video › Presets*:
  - **Automatic** (default): picks the renderer settings for each film from the engine, the graphics card or Mac (chip, GPU cores, fan, battery, heat on macOS), the file's resolution versus its size on screen, bitrate per pixel and codec, and frame rate. The chosen level is shown in grey next to the item. **macOS:** when the film needs upscaling, power goes first to the upscaler (NGU Anti-Alias and clean-up for poor sources, NGU Sharp then NGU chroma and SuperChromaRes for good ones).
  - **Custom**: your own automations, like Shortcuts on the iPhone, e.g. "When the film is 4K and the PC is on battery → madVR: High", on resolution, HDR, frame rate, power source and screen.
  - **Turn off presets**: only your Settings apply.
  - Ready-made **Low, Medium, High, Ultra** and **DVD and low quality** presets for each renderer, editable from *Settings › Components › Presets*; new presets start every entry from "As in Settings". A preset never changes your saved Settings.
- **HDR**: one choice for all engines: Auto, passthrough to the display, HDR → SDR tone mapping, or HDR → SDR with a madVR 3DLUT; madVR HDR profiles.
- **Windows: NVIDIA RTX Video Super Resolution** (up to SD, 720p, 1080p or 1440p) and **RTX Video HDR** with MPC Video Renderer, from Settings, the right-click menu and the remote.
- **macOS: upscaling** with madVR beyond the native resolution, from the menu.
- **Display refresh rate**: automatic switching to the film's frame rate before it starts, with exact rational rates (23.976 stays 23.976; on macOS also when the display offers only 24.000 Hz), restored on exit (Windows).
- **3D**: automatic detection of Side-by-Side and Top/Bottom; show one eye or keep both (3D projector); extended full screen across two or more screens (`Shift+F`).
- **Statistics**: renderer statistics (`Ctrl+J` for madVR, with reset on macOS) and mpv statistics, from the menu.
- **Component settings inside the player**: native pages for madVR (including the **Performance** tab), MPC Video Renderer, LAV Video, LAV Audio and mpv; **Windows** also XySubFilter, MPC Audio Renderer and NVIDIA RTX.
- **macOS: film screen**: choose on which screen the film opens.

## 3. Full-screen image

- **Sizing**: constant height, constant area or custom (macOS also *Fill*, with a slider between constant height and constant area). `Z` cycles the modes during the film.
- **Black bars**: the frame's real aspect ratio is measured live, even when it changes mid-film; **IMAX scenes** expand to full screen.
- **Crop** from *Video › Crop* (Windows: 4:3, 16:9, 1.85, 2.00, 2.20, 2.35, 2.39 or 2.76).
- **Windows: full screen like a zoom**: the window grows to fill the screen and shrinks back, with the film visible throughout; no black flash.
- **macOS:** animated transition when the format changes; optional start in full screen; system full screen of macOS with a clean switch through black.

## 4. Playback controls

- **HUD**: timeline with frame previews (on discs, the time) and chapters, volume, full screen, 10-second skips. **Windows:** previews are decoded by the graphics card, also on network folders.
- **Fast scan**: holding a skip button scrolls at ×0.5 … ×4 (Windows: each click changes speed or direction, Play ends it).
- **Frame step**: `,` and `.` when paused.
- **Go to…** a precise time (macOS); chapters from the menu and with PgUp / PgDn.
- **Resume where you left off**, with *Continue watching* in the library.
- **Per-film volume**, amplification above 100% up to +12 dB with all engines.
- **Info panel**: the film first (poster, year, duration, rating, plot, cast), then source, decoder, output, live video/audio bitrate (also YouTube, DLNA, Jellyfin), dropped frames.
- **Cast during the film**: photos and characters.
- **Skip Intro / Next episode**: detected automatically for series (Windows: offline, by comparing audio fingerprints across a season's episodes).
- **Stream quality switch** for YouTube, DLNA and Jellyfin/Emby, reloaded in place from the same point.
- **Right-click menu** in glass style with every video, audio, subtitle and extra option, translated down to the last level **(4.1)**; entries stay locked until they can be used.
- **Low Power Mode / Battery Saver**: the player offers a lighter renderer for the session, with *Don't show again* **(4.1)**; with a preset active the presets handle it.
- **Media keys** and system media controls (Windows SMTC, macOS *Now Playing*).
- **Windows:** the video Picture in Picture was removed in 4.1 (it froze with madVR); the music mini player stays.

## 5. Film audio

- **Audio output** and device, changeable mid-film without restarting the video (rebuilding only the audio on macOS).
- **Bitstream**: **Windows:** chosen track by track, or *Auto* / *Force PCM*; Dolby Digital, DD+ (Atmos), DTS, TrueHD, DTS-HD MA, DTS:X; **MPC Audio Renderer** is used when installed, with automatic bitstreaming **(4.1)**. **macOS (HDMI):** Dolby Digital, Dolby Digital Plus (also Atmos) and DTS to the receiver, with *Auto (bitstream if worthwhile)* or *Force PCM*; DTS-HD and DTS:X are decoded; TrueHD is decoded except on adapters that carry HBR (experimental *TrueHD and Atmos* option).
- **Audio tracks** from the menu and from the phone.
- **Audio delay** on the fly in 10 and 100 ms steps, saved per film; automatic synchronization between internal tracks, also with drifting delay (Windows: fixed or **curved** delay for rips that drift).
- **External audio**: a separate audio file (e.g. a dub) is aligned automatically by comparing the two tracks, with 24 / 23.976 / 25 fps speed conversion, and saved for that film.
- **Amplification** up to +12 dB.
- **macOS: spatial audio**: on headphones (AirPods with head tracking) and on the Mac speakers, 5.1 and 7.1 go through Apple's spatializer. DTS:X arrives as 7.1.4 with the height channels; TrueHD Atmos is decoded by LibTrueHD and rendered as 7.1.4. Works with all engines.
- **Windows: real-time audio analysis**: levels, true peak, correlation, balance, stereo width, also with bitstream active.
- **Network receiver volume** (section 14) with the ↑ ↓ keys.

## 6. Subtitles

- **Internal and external tracks**, forced-only or manual; `.srt`, `.ass`, `.ssa`, `.sub`/`.idx`, `.sup`, `.vtt`; **Windows:** full ASS/SSA through XySubFilter.
- **Search and download** from the player: YIFY and Addic7ed (Windows: without an account), Subdl with your free key, OpenSubtitles with your account. **Windows:** 34 languages.
- **Default language** for subtitles.
- **Automatic re-timing** by listening to the film's audio, also on an already loaded subtitle.
- **Text style and position** from the settings.

## 7. Library

- **Sections**: Home (rotating banner and *Continue watching*), Movies, TV Series, Music, Photos, Videos, Playlists, Favourites, Diary, Network; **macOS** also YouTube. Global search.
- **Source folders** and external volumes added and removed from the interface; **Windows:** network and rclone drives handled gently; **macOS:** a default library source (the Mac or a network server).
- **TMDb metadata** in the app's language (English, Italian, Spanish): posters, backdrops, plots, genres, cast, directors, episodes. Changing language updates the cards without losing covers or manual fixes. Requires your own free TMDb key.
- **Manual *Fix title and cover***: title, year, TMDb search, poster from those available or from your own file.
- **Title pages**: overview, file technical details, reviews and ratings (TMDb, IMDb, Letterboxd, Metacritic), cast and crew, recommendations.
- **Collections, filters and sorting** by genre, year, decade, director, artist, folder; quick picks (Lossless, Atmos, Rated); sorting by name, date, duration, size.
- **Diary** of what you watched, with removal of single entries; *Hide from library*, *Show in Finder* (macOS), *Open with*.
- **Playlists** of video, music, photos or mixed; editable name, type, cover and description; editable **play queue**; *Add to playlist*, *Play from here*.
- **Library report**: totals, space, formats, plus cases to check (duplicates, low resolution or bitrate, inconsistent HDR metadata, broken files).
- **Trakt**: link with a code, personal recommendations (*Mark as watched*, *Not interested*), history and ratings sync.
- **macOS: first-launch guide**: welcome, folders, TMDb key, extra features.

## 8. Spotlight and Cinema Mode

- **Spotlight**: full-screen discovery view with large backgrounds prepared in advance, library or network source, search, favourites and cast; started titles first; usable from the remote; **macOS:** can open at launch.
- **Cinema Mode**: pre-film screen (also for discs, from the disc label; on macOS with TMDb's automatic background) and optional demo clip before the screening, chosen or random, from your own folders.

## 9. Network: Jellyfin, Emby, DLNA, Plex

- **Automatic discovery** of DLNA/UPnP, Jellyfin, Emby and Plex servers on the network, or an address added by hand.
- **Jellyfin**: login with username and password or **Quick Connect**, the server's catalogue and metadata, original-file playback, resume point and watched status synced.
- **Emby**: the same, except Quick Connect.
- **Reduced quality**: with a bitrate limit the server transcodes on the fly.
- **DLNA and Plex**: browsing and playback.
- Access tokens stored encrypted (Windows DPAPI, macOS Keychain), never written to logs.

## 10. Music

- **Cinecore Audio Engine**: FFmpeg decoding, double-precision DSP; **Windows:** WASAPI output; **macOS:** Core Audio output. Shared, **exclusive** or **bit-perfect**.
  - 10- or 31-band graphic EQ or parametric EQ (frequency, gain, Q; graph editor on Windows), presets, preamp.
  - ReplayGain per track or album, crossfeed, stereo width, balance, mono, loudness compensation / dynamic AutoEQ, true-peak limiter.
- **Gapless** (also exclusive/bit-perfect), **crossfade** (Windows: 3, 6 or 10 s, skipped between tracks of the same album) and silence skipping (macOS).
- **Automix**: beat grids, phrase boundaries, key and vocal activity pick the cue points and the transition; tempo matched with Rubber Band without changing pitch; on macOS also inside albums, with style and intensity (three-band EQ, filters, echo, brake, loop). **Crossfade only** style, and no club effects on soundtracks, piano and non-dance music. *Sort queue for mix* by tempo, key and energy.
- **Manual mix editor**: both waveforms with beat markers, cue points, tempo and effects; **draw volume, filter and effect curves by hand** and the incoming track follows the outgoing tempo **(4.1)**. A saved transition is reused every time those two tracks play in that order (Windows).
- **Radio**: when the queue ends, similar tracks from your own library, offline.
- **Lyrics**: LRCLIB, Genius, lyrics.ovh, synced when available; unsynced lyrics aligned to the vocals (Windows: with faster-whisper, optional, with repeated choruses; macOS: *Sync the lyrics* in the ••• menu of the bar). **(4.1, Windows)** *Tools › Genius: verify connection…* when the site asks for a check.
- **Artist photos and covers**: from the files, then Spotify, TIDAL (and Deezer on Windows), with your own keys (images and metadata only, no streaming).
- **Music home**: rotating artist banner, *Continue listening* with album progress, favourites, `.wpl` playlists (cover from the first four albums on macOS).
- **Meters**: spectrum, spectrogram, waveform, LUFS, stereo, levels and VU with PEAK/CLIP; **macOS** also dynamics, goniometer, phase correlation, live integrated LUFS, true peak, crest and PLR; true peak over the whole track and steadier readouts **(4.1, Windows)**.
- Full-screen track view, **mini player** (always on top on Windows), glass bar and queue (history, now playing, up next, drag to reorder), repeat, shuffle.
- **Scrobbling** to Last.fm and ListenBrainz, queued while offline.

## 11. Photos

- **Viewer**: filmstrip, zoom, rotation, mirror, slideshow, first/last photo, favourites, Info panel (camera, exposure, place in Maps).
- **Editor**: adjustments, filters, crop and drawing (macOS: capsule bar with pen, shapes, text, filters); always saves a copy, never changes the original.
- **Actions**: share, copy, open with, print, set as desktop picture, move to the Recycle Bin (Windows) or Trash (macOS), *Show in Finder* (macOS).

## 12. HDR analysis

- **Declared metadata**: mastering display, MaxCLL, MaxFALL, Dolby Vision, HDR10+ (Dolby Vision values fixed in 4.1 on Windows).
- **Full-film measurement**: peak and average nits, percentiles, brightness distribution, share outside Rec.709 and DCI-P3 / BT.2020.
- **Live scopes**: RGB waveform, vectorscope, CIE 1931, false colour; up to 24 measurements per second.
- **Export** to a file (Windows: HTML, PNG, CSV, JSON).

## 13. Phone remote

- **Web page served by the player**: pair with a QR code (and PIN on Windows), nothing to install; on Windows it can be added to the Home screen and is reachable also through a `….local` name. **macOS:** if the phone cannot connect, change the address (the Mac's name is recommended).
- **Controls**: play, pause, stop, skips, fast scan, chapters, volume (Windows: with the amplifier's dB), full screen, D-pad (also DVD menus), keyboard.
- **Trackpad**: moves the pointer, clicks and scrolls (Windows: inertial two-finger scrolling; macOS: needs the Accessibility permission).
- **Tracks**, **library**, **queue** and **playlists**, from the computer or a network server; on macOS also the full Spotlight.
- **Settings** with the same menus as the player (*Player* and *Components*), only the options of that system, plus the *This computer* monitor (CPU, memory, GPU, disks, network, battery) **(4.1)**.

## 14. Home devices

- **Network AV receiver**: Denon/Marantz, plus (Windows) Yamaha MusicCast, Onkyo/Integra/Pioneer, NAD, Anthem and UPnP, for films and music. Safety rules: reads the volume before each change, max +3 dB per step, ceiling at −20 dB; on Windows a film can only lower the volume at start and a mute set during a film is removed when it closes. On macOS it is tied to a Mac audio output.
- **WLED lights**: dim or turn off during the film.
- **Automations**: HTTP (GET/POST) or MQTT on start, pause, resume, stop and end credits (e.g. Home Assistant), following the session.

## 15. System, services and updates

- **Interface**: light or dark theme (Windows: or the system theme), accent colour with a colour picker and typed or pasted hex code; macOS also background colour and top bar height (automatic on small screens).
  - **Windows:** acrylic glass, live in the music bar, Photos and menus **(4.1)**; animations.
  - **macOS:** **Liquid Glass** from macOS 26 and real blur on earlier versions.
- **Languages**: **English, Italian, Spanish**, complete across the app, menus and track names.
- **Updates** of the app from GitHub Releases (Windows: with SHA-256 verification; macOS: with an optional check at startup), plus updates of third-party components (yt-dlp; on macOS also the madVR/MPC-VR engine). **Windows:** an *External components* page shows the installed versions.
- **Open Cinecore at startup** (Windows: Start with Windows; macOS: at Mac startup), optional.
- **Keys and tokens**: never in plain text in files or logs. Personal keys (TMDb, Subdl, OpenSubtitles, Spotify, TIDAL) are entered by each user and stored encrypted (Windows DPAPI, macOS Keychain). **Windows:** the program contains only the application's own keys (Trakt and Last.fm, not in the public source).
- **Lighter interface**: pages redraw only when something changes. **(4.1, Windows)** Idle CPU from about 5.5% to about 2.4%, idle Spotlight from 56% to 3%. **macOS:** external processes start without disturbing the film, and the engine windows stay in the full-screen Space.
- **No telemetry.**

## 16. Keyboard shortcuts

| Key | Action |
|---|---|
| Space | Play / pause (in photos on macOS: slideshow) |
| ← → | Back / forward 10 seconds (in photos: previous / next) |
| `,` `.` | Previous / next frame |
| PgUp PgDn | Next / previous chapter |
| ↑ ↓ | Volume, above 100% amplification (or the network receiver's volume) |
| M | Mute (on a DVD: disc menu) |
| F | Full screen |
| Shift+F | Extended full screen |
| Z | Image sizing |
| Ctrl + / Ctrl − | Audio delay of 10 ms (with Shift: 100 ms) |
| Ctrl 0 | Reset audio delay |
| Ctrl+J | Renderer statistics |
| O / Ctrl+O (macOS: O / Cmd+O) | Open file |
| S | Close and return to the library |
| Cmd+W (macOS) | Close |
| Esc | Back / exit full screen |
| Arrows, Enter, Back | DVD menus |
| In photos: + − 0 | Zoom, initial view |
| In photos: R (macOS: also Shift+R, H) | Rotation (macOS: reverse rotation, mirror) |
| In photos on macOS: F, I, E, Delete | Favourite, info, edit, Trash |
| In photos: Home / End | First / last photo |
