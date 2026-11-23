# Cinecore Player: All Features

Reference version: **Windows Beta 4.1 (0.4.1)**, November 2026.
Features added in Beta 4.1 are marked **(4.1)**.

## 1. What it plays

- **Local video**: MKV, MP4, M2TS, TS, MOV, AVI, WMV, WebM and the other formats read by LAV and mpv.
- **Local music**: MP3, FLAC, WAV, OGG, Opus, M4A, AAC, WMA, DSF, APE and others.
- **Photos**: full-window viewer with filmstrip and slideshow.
- **Discs**: Blu-ray and DVD from an optical drive, from a folder (BDMV or VIDEO_TS) and from an ISO image. Protected Blu-rays open only if the user has installed MakeMKV or imported their own key file: the player does not include any.
- **DVDs like on a standalone player**: disc menus and special features with every engine, in the correct format (16:9 or 4:3 anamorphic). Navigate with the arrows, Enter and Back, the mouse or the phone remote; `M` returns to the disc menu.
- **Disc resume**: reopening a partly watched disc shows a full-screen page with the movie's artwork and *Resume* / *Start over*.
- **Discs in the library**: the inserted disc appears among the movies or albums and under Devices while it stays in the drive.
- **Audio CDs**: open like an album, identified through MusicBrainz (titles, artist, year, cover; saved for next time), read losslessly and played by the Cinecore Audio Engine (gapless, exclusive/bit-perfect, EQ, lyrics, scrobbling).
- **Network servers**: Jellyfin, Emby, DLNA/UPnP (Plex included).
- **YouTube and other sites** via yt-dlp, with a resolution limit from 144p to 8K. A single continuous load, standard quality names (2160p, 1440p, 1080p…), audio always at the best quality; the player checks which streams are accepted before opening.
- **Files from the system**: *Open with*, double-click and drag-and-drop; if the player is already open, the file goes to the existing window. Videos, music and photos open straight away, without the startup screen **(4.1)**.

## 2. Video engines

- **Three engines**: **madVR**, **MPC Video Renderer** (DirectShow with LAV and XySubFilter) and **mpv**. One default engine plus a per-session choice.
- mpv with NVIDIA decoding (NVDEC) on the graphics card **(4.1)**.
- **Renderer presets (4.1)** in the right-click menu, *Video › Presets*:
  - **Automatic** (default): picks the renderer settings for each film from the graphics card, the file's resolution versus its size on screen, bitrate and codec, frame rate and Low Power Mode. The chosen level is shown in grey next to the item.
  - **Custom**: your own automations, e.g. "When the film is 4K and the PC is on battery → madVR: High".
  - **Turn off presets**: only your Settings apply.
  - Ready-made **Low, Medium, High, Ultra** and **DVD and low quality** presets for madVR, MPC Video Renderer and mpv, editable from *Settings › Components › Presets*. A preset never changes your saved Settings; picking one restarts the film from the same point.
- **0 dropped frames at startup and after seeking** with all three engines **(4.1)**; madVR starts with its render queue full.
- **"madVR is stuttering" warning (4.1)**: if madVR keeps dropping frames, the player offers a lighter engine. It never switches on its own.
- **HDR**: passthrough to the display, HDR → SDR with pixel shaders or a 3DLUT, madVR HDR profiles.
- **NVIDIA RTX Video Super Resolution** (up to SD, 720p, 1080p or 1440p) and **RTX Video HDR** with MPC Video Renderer, from Settings, the right-click menu and the remote.
- **Display refresh rate**: automatic switching to the film's frame rate before it starts, with exact rational rates (23.976 stays 23.976), restored on exit.
- **3D**: automatic detection of Side-by-Side and Top/Bottom; keep both eyes for a 3D projector or show one; extended full screen across several screens (`Shift+F`).
- **Component settings inside the player**: native pages for madVR (including the **Performance** tab with the DXVA and 3DLUT trade-offs **(4.1)**), MPC Video Renderer, LAV Video, LAV Audio, XySubFilter, mpv, MPC Audio Renderer and NVIDIA RTX.

## 3. Full-screen image

- **Sizing**: constant height, constant area or custom. `Z` cycles the modes during the film.
- **Black bars**: the frame's real aspect ratio is measured live, even when it changes mid-film; **IMAX scenes** can expand.
- **Crop** to 4:3, 16:9, 1.85, 2.00, 2.20, 2.35, 2.39 or 2.76 from *Video › Crop*.
- **Full screen like a zoom**: the window grows to fill the screen and shrinks back, with the film visible throughout; no black flash.

## 4. Playback controls

- **HUD**: timeline with frame previews (decoded by the graphics card, also on network folders) and chapters, volume, full screen, 10-second skips.
- **Fast scan**: holding a skip button scrolls at ×0.5, ×1, ×2, ×4; each click changes speed or direction, Play ends it.
- **Frame step**: `,` and `.` when paused.
- **Resume where you left off**, with *Continue watching* in the library.
- **Per-film volume**, amplification above 100% up to +12 dB.
- **Info panel**: the film first (poster, year, duration, rating, plot, cast), then source, decoder, output, live video/audio bitrate (also YouTube, DLNA, Jellyfin), dropped frames.
- **Cast during the film**: a HUD button opens the cast with photos and characters.
- **Skip Intro / Next episode**: detected offline by comparing audio fingerprints across a season's episodes.
- **Stream quality switch** for YouTube, DLNA and Jellyfin/Emby, reloaded in place from the same point.
- **Right-click menu** in glass style with every video, audio, subtitle and extra option, translated down to the last level **(4.1)**.
- **Low Power Mode / Battery Saver**: the player offers a lighter renderer than madVR for the session, with *Don't show again* **(4.1)**; with a preset active the presets handle it.
- **Media keys** and Windows media controls (SMTC).
- The video Picture in Picture has been removed in 4.1 (it froze with madVR); the music mini player stays.

## 5. Film audio

- **Audio output** and device, changeable mid-film without restarting the video.
- **Bitstream** chosen track by track, or *Auto* / *Force PCM*.
  - Dolby Digital, DD+ (Atmos), DTS, TrueHD, DTS-HD MA, DTS:X. **MPC Audio Renderer** is used when installed, with automatic bitstreaming **(4.1)**.
- **Audio delay** on the fly in 10 and 100 ms steps, saved per film; fixed or **curved** delay for rips that drift.
- **External audio**: a separate audio file (e.g. a dub) is aligned automatically by comparing the two tracks, with 24 / 23.976 / 25 fps speed conversion, and saved for that film.
- **Real-time audio analysis**: levels, true peak, correlation, balance, stereo width, also with bitstream active.

## 6. Subtitles

- **Internal and external tracks**, forced-only or manual; full ASS/SSA through XySubFilter.
- **Search and download**: YIFY (films) and Addic7ed (series) without an account, Subdl with your free key, OpenSubtitles with your account. **34 languages**, with a default language.
- **Automatic re-timing** by listening to the film's audio, also on an existing subtitle.
- **Text style and position** from the settings.

## 7. Library

- **Sections**: Home (rotating hero), Movies, TV Series, Music, Photos, Videos, Playlists, Network, Favourites, Diary; global search.
- **Source folders** added and removed from the interface; network and rclone drives handled gently.
- **TMDb metadata** in the app's language (English, Italian, Spanish) **(4.1)**: posters, backdrops, plots, genres, cast, directors, episodes. Changing language updates the cards without losing covers or manual fixes. Requires your own free TMDb key.
- **Manual *Fix title and cover***: title, year, TMDb search, poster from those available or from your own file.
- **Title pages**: overview, file technical details, reviews and ratings (TMDb, IMDb, Letterboxd, Metacritic), cast, recommendations.
- **Collections and filters** by genre, year, decade, director, artist, folder; quick picks (Lossless, Atmos, Rated); sorting by name, date, duration, size.
- **Continue watching** and **Diary**, with removal of single entries.
- **Playlists** of video, music, photos or mixed, shown like an album page; editable name, type, cover and description; editable **play queue**.
- **Library report**: totals, space, formats, plus cases to check (duplicates, low resolution or bitrate, inconsistent HDR metadata, broken files).
- **Trakt**: personal recommendations (*Mark as watched*, *Not interested*), history and ratings sync.

## 8. Spotlight and Cinema Mode

- **Spotlight**: full-screen discovery view with large backgrounds, library or network source, search and cast; started titles first; usable from the remote.
- **Cinema Mode**: pre-film screen (also for discs, from the disc label) and optional demo clip before the screening.

## 9. Network: Jellyfin, Emby, DLNA

- **Jellyfin**: discovery on the network, login with username and password or **Quick Connect**, the server's catalogue and metadata, original-file playback, resume point and watched status synced.
- **Emby**: the same, except Quick Connect.
- **Reduced quality**: with a bitrate limit the server transcodes on the fly.
- **DLNA/UPnP**: automatic discovery, browsing and playback.
- Access tokens stored encrypted, never written to logs.

## 10. Music

- **Cinecore Audio Engine**: FFmpeg decoding, double-precision DSP, **WASAPI** output: shared, exclusive or bit-perfect.
  - 10- or 31-band graphic EQ or parametric EQ with a graph editor, presets.
  - ReplayGain per track or album, crossfeed, stereo width, balance, mono, loudness compensation / dynamic AutoEQ, true-peak limiter.
- **Gapless** (also exclusive/bit-perfect) and **crossfade** (3, 6 or 10 s, skipped between tracks of the same album).
- **Automix**: beat grids, phrase boundaries, key and vocal activity pick the cue points and the transition; tempo matched with Rubber Band without changing pitch. **Crossfade only** style, and no club effects (echo, tape stop) on soundtracks, piano and non-dance music **(4.1)**. *Sort queue for mix* by tempo, key and energy.
- **Manual mix editor**: both waveforms with beat markers, cue points, tempo and effects; **draw volume, filter and effect curves by hand** and the incoming track follows the outgoing tempo **(4.1)**. A saved transition is reused every time those two tracks play in that order.
- **Radio**: when the queue ends, similar tracks from your own library, offline.
- **Lyrics**: LRCLIB, Genius, lyrics.ovh; unsynced lyrics aligned to the vocals with faster-whisper (optional), with repeated choruses. *Tools › Genius: verify connection…* when the site asks for a check **(4.1)**.
- **Artist photos and covers**: from the files, then Spotify, TIDAL or Deezer (images and metadata only, no streaming).
- **Music home**: rotating artist banner, *Continue listening* with album progress, favourites, `.wpl` playlists.
- **Meters**: spectrum, spectrogram, waveform, LUFS, stereo, levels and VU with PEAK/CLIP; true peak over the whole track and steadier readouts **(4.1)**.
- Full-screen track view, **mini player** (frameless glass window, always on top), repeat, shuffle.
- **Scrobbling** to Last.fm and ListenBrainz, queued while offline.

## 11. Photos

- **Viewer**: filmstrip, zoom, rotation, slideshow, first/last photo, Info panel (camera, exposure, place in Maps).
- **Editor**: adjustments, filters, crop and drawing; always saves a copy, never changes the original.
- **Actions**: share, copy, open with, print, set as desktop picture, move to Recycle Bin.

## 12. HDR analysis

- **Declared metadata**: mastering display, MaxCLL, MaxFALL, Dolby Vision, HDR10+ (Dolby Vision values fixed in 4.1).
- **Full-film measurement**: peak and average nits, percentiles, brightness distribution, share outside Rec.709 and DCI-P3 / BT.2020.
- **Live scopes**: RGB waveform, vectorscope, CIE 1931, false colour; 24 measurements per second **(4.1)**.
- **Export** to HTML, PNG, CSV, JSON.

## 13. Phone remote

- **Web page served by the player**: pair with a QR code and PIN, nothing to install; can be added to the Home screen. Reachable also through a `….local` name, no port needed.
- **Controls**: play, pause, stop, skips, fast scan, chapters, volume (with the amplifier's dB), full screen, D-pad (also DVD menus), keyboard, **trackpad** with inertial two-finger scrolling.
- **Tracks**, **library**, **queue** and **playlists**, from the PC or a network server.
- **Settings** with the same menus as the player (*Player* and *Components*), only the options of that system **(4.1)**.
- **This computer**: live CPU, memory, GPU, disks, network, battery.

## 14. Home devices

- **Network AV receiver**: Denon/Marantz, Yamaha MusicCast, Onkyo/Integra/Pioneer, NAD, Anthem, UPnP, for films and music. Safety rules: reads the volume before each change, max +3 dB per step, ceiling at −20 dB, a film can only lower the volume at start; mute set during a film is removed when it closes.
- **WLED lights**: dim or turn off during the film.
- **Automations**: HTTP (GET/POST) or MQTT on start, pause, resume, stop and end credits (e.g. Home Assistant), following the session.

## 15. System, services and updates

- **Interface**: light, dark or Windows theme, accent colour (colour picker with typed or pasted hex code **(4.1)**); Windows glass (acrylic), live in the music bar, Photos and menus **(4.1)**; animations.
- **Languages**: **English, Italian, Spanish**, complete across the app, menus and track names **(4.1)**.
- **Updates** from GitHub Releases with SHA-256 verification.
- **External components** page with the installed versions.
- **Start with Windows**, optional.
- **Lighter interface (4.1)**: pages redraw only when something changes; idle CPU from about 5.5% to about 2.4%, idle Spotlight from 56% to 3%.
- **Keys and tokens**: never in plain text in files or logs. The program contains only the application's own keys (Trakt and Last.fm, not in the public source); personal keys (TMDb, Subdl, OpenSubtitles, Spotify, TIDAL) are entered by each user and stored encrypted with Windows DPAPI.
- **No telemetry.**

## 16. Keyboard shortcuts

| Key | Action |
|---|---|
| Space | Play / pause |
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
| O / Ctrl+O | Open file |
| S | Close and return to the library |
| Esc | Back / exit full screen |
| In photos: + − 0 R | Zoom, initial view, rotation |
| In photos: Home / End | First / last photo |
