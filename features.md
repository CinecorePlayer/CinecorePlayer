# Cinecore Player: All Features

Reference version: Beta 3.6 (0.3.6), October 8, 2026.
List compiled from the player's code, item by item. Features added in 3.6 are marked **(3.6)**.

## 1. What it plays

- **Local video**: MKV, MP4, M2TS, TS, MOV, AVI, WMV, WebM and the other formats read by LAV and mpv.
- **Local music**: MP3, FLAC, WAV, OGG, Opus, M4A, AAC, WMA and others.
- **Photos**: full-window viewer with slideshow.
- **Discs**: Blu-ray and DVD from an optical drive, from a folder (BDMV or VIDEO_TS) and from an ISO image, which is mounted automatically without administrator permissions. Protected Blu-rays open only if the user has installed MakeMKV or imported their own key file: the player does not include any.
- **DVDs like on a standalone player (3.6)**: initial warnings, disc menus and special features, with all renderers (madVR, MPC Video Renderer, EVR) and in the correct format (16:9 or 4:3 anamorphic), even when switching between windowed and full screen. In menus you navigate with the keyboard's arrows, Enter and Back, with the mouse, or with the arrows on the phone remote; the M key or the Disc menu item returns to the menu. Chapters, audio tracks and subtitles are chosen from the player's menu; menus and audio start in the interface language when the disc offers it. Requires the drive's region to match the disc's; if the disc doesn't start, the player switches to direct reading on its own.
- **Direct DVD reading (3.6)**: in Settings › General you can choose to go straight to the movie without menus (using mpv), selecting chapters, audio, subtitles and disc titles from the player's menu.
- **Disc resume (3.6)**: when reopening a partly watched disc, a full-screen page appears with the movie's artwork and a Resume or Start over choice, like on a standalone player (arrows and Enter, mouse or remote). On the disc timeline the time under the pointer is shown, without a preview frame (a second reader on the same disc would stop the movie).
- **Video cropping (3.6)**: from Video › Crop you choose the aspect ratio (4:3, 16:9, 1.85, 2.00, 2.20, 2.35, 2.39, 2.76) and the image is cropped at the center, with madVR, MPC Video Renderer, EVR and mpv; it applies to the current movie.
- **Loading screens (3.6)**: background and movie title with a full-width progress bar that reaches the end before playback starts; for CDs, the album cover and then the wide artist photo.
- **Music bar (3.6)**: centered, rounded glass island, with the page visible around it.
- **Remote (3.6)**: time can be typed by tapping the current minute, visible touch feedback on buttons, no system text selection on iPhone; on screen, a compact pill for volume (with the amplifier's dB) and messages, and the player's timeline follows dragging from the phone.
- **Spanish (3.6)**: third interface language, from Settings › General › Language; about 1,300 strings translated. The remote page, some messages composed on the fly and TMDb plots remain in English.
- **Cast during the movie (3.6)**: a button in the playback overlay opens the cast with photos and characters.
- **Info panel (3.6)**: the movie first (poster, year, duration, rating, plot and cast), then the technical data.
- **Discs in the library (3.6)**: the inserted disc appears among the movies or albums and as an entry under Devices, as long as it stays in the drive.
- **Audio outputs (3.6)**: fixed the freeze when connecting an audio device while a CD is playing (only one reader on the disc, the copy for lyrics yields to the music, the reader is queried only when Windows signals a change); if the output in use disappears, the track reopens by itself at the same point.
- **Full screen and audio analysis (3.6)**: switching to full screen and back without the intermediate window; the loading image no longer shows through under the movie; the chart selector has an equal underline under each item, gray, colored on the selected one; library, charts, text, analysis items and the PiP frame cross-fade into each other; returning from Spotlight with music playing no longer leaves a black screen.
- **Loading (3.6)**: the bar reaches the end before the movie starts and moves smoothly; in the startup logo the last letter no longer pops in abruptly.
- **Interface (3.6)**: plots and cast rows with a typeface designed for small sizes; light/dark theme change with a cross-fade; text-only audio chart selector with colored underline; full screen right below Play in the right-click menu.
- **Spotlight (3.6)**: started and resumable titles come first, most recent first; when empty, a page explains what to add and leads to the library; the Library / Network switch always follows the saved source.
- **Audio CDs (3.6)**: the CD in the drive opens like an album (from Open › Open disc, from the drive or from one of its tracks), with the tracks queued. The disc is identified by its track index through MusicBrainz, which provides titles, artist, year and cover; without a network or if the disc isn't in the database, Track 1, 2… remain. Audio is read from the disc losslessly and played by the Cinecore Audio Engine, so with gapless playback between tracks, exclusive or bit-perfect output, equalizer, lyrics and scrobbling. Data for a disc already seen stays saved: the next time it opens without a network.
- **Pre-movie screen for discs too (3.6)**: if enabled, the disc is introduced with the movie's title and background taken from TMDb based on the disc label.
- **Network servers**: DLNA, Jellyfin and Emby **(3.6)**.
- **YouTube**: via yt-dlp, with a resolution limit from 144p to 8K.
- **Files opened from File Explorer**: "Open with", double-click and drag-and-drop; if the player is already open, the file is passed to the existing window.

## 2. Video engines

- **Four engines to choose from**: madVR, MPC Video Renderer, EVR (all on DirectShow with LAV) and mpv. "Auto" chooses on its own; you can set one engine for the session and a default one.
- **HDR**: passthrough to the display, RTX Video HDR with MPC Video Renderer, HDR → SDR conversion with pixel shader or with 3DLUT, madVR HDR profiles.
- **Upscaling**: madVR or NVIDIA RTX Super Resolution, with profiles.
- **Display refresh rate**: automatic switching to the movie's frame rate, including fractional values like 23.976 Hz.
- **3D**: automatic detection of Side-by-Side and Top/Bottom, conversion to 2D, native output, extended full screen across multiple monitors (one eye per screen).
- **MKV files with "disabled" tracks**: served to the player with the correct flag, without rewriting the file.
- **Component settings inside the player**: native pages for madVR, LAV Video, LAV Audio, MPC Video Renderer, MPC Audio Renderer, XySubFilter and mpv.

## 3. Full-screen image

- **Sizing**: Fill, constant height, constant area and custom, with a slider between constant height and constant area and a per-format multiplier. `Z` key during the movie.
- **Black bars**: measurement of the frame's real aspect ratio, even when it changes mid-movie.
- **IMAX scenes**: can expand to full screen.
- **Animated transition** when the aspect ratio changes, with selectable duration.
- **Full-screen start** of the player, on the screen where the mouse is.
- **Screen adaptation**: the interface resizes itself when changing monitor or Windows scale.

## 4. Playback controls

- **HUD**: timeline with frame preview, chapters, volume, full screen, 10-second skips.
- **Fast scan (3.6)**: holding a skip button starts fast scrolling (x0.5, x1, x2, x4); each click increases the speed or reverses direction, Play ends it.
- **Single step (3.6)**: `,` and `.` move one frame back or forward, when paused.
- **Resume where you left off**, for each title, with "Continue watching" in the library.
- **Per-movie volume**: the level chosen during a movie is remembered for that movie.
- **Amplification above 100%** up to +12 dB.
- **Info panel**: source, output, decoding, audio, dropped frames, stream origin; also works on DLNA, Jellyfin and Emby.
- **Intro and end credits**: automatic detection for series (audio comparison between episodes) and a button to skip them.
- **PiP mode**: small always-on-top window with essential controls.
- **Right-click menu** in glass style, with all video, audio, subtitle and extra options.
- **Media keys** on the keyboard and shortcuts (list in chapter 16).

## 5. Movie audio

- **Audio output** and device selection, with return to the PC's default.
- **Bitstream** (Dolby, DTS, TrueHD, DTS-HD) to the amplifier or forced PCM; "Auto" decides case by case.
- **Audio tracks**: selected from the menu and from the remote.
- **Audio delay** adjustable on the fly, in 10 and 100 ms steps.
- **External audio**: a separate audio file can be paired with the movie; synchronization is found automatically by comparing the two tracks and is saved for that movie.
- **Real-time audio analysis**: levels, true peak, correlation, balance, stereo width; with bitstream active, the analysis works on a parallel decode.

## 6. Subtitles

- **Internal and external tracks**, automatic selection (forced only) or manual, XySubFilter with DirectShow.
- **Search and download** from the player: YIFY Subtitles for movies and Addic7ed for series without an account; **Subdl for movies and series (3.6)** with the free key from your profile; OpenSubtitles with your own account.
- **34 languages** in search, with a selectable default language.
- **Automatic realignment**: the downloaded file is synced by listening to the movie's audio; it can be redone on an existing subtitle.
- **Text style and position** from the settings.

## 7. Library

- **Sections**: Home, Movies, TV Series, Music, Photos, Videos, Playlists, Network.
- **Source folders** for movies and other content, added and removed from the interface.
- **Metadata from TMDb**: posters, backgrounds, plot, genres, cast, directors, series episodes. Requires your own free TMDb key, entered in Settings › General › Metadata **(3.6)**.
- **Manual correction**: title, year, TMDb search and poster selection from those available or from your own file.
- **Title page**: technical details of the file, cast, reviews and ratings from TMDb, IMDb, Letterboxd and Metacritic.
- **Collections and filters**: by genre, year, decade, director, artist, folder; sorting by name, date, duration, size; favorites, recent, in progress, rated.
- **Global search** from Home across the whole library.
- **Continue watching and Diary** of what has been watched, with removal of individual entries.
- **Playlists** of video, music, photos or mixed, and an editable **play queue**.
- **Library report**: how many titles, how much space, which formats, plus cases to check (duplicates, low resolution or bitrate, HDR with inconsistent metadata, files that won't open).
- **Light, dark or Windows theme**, accent color and customizable palette.

## 8. Spotlight and cinema mode

- **Spotlight**: full-screen theater-style interface, with large backgrounds, choice between the PC library and network servers, search and cast; can open at startup.
- **Cinema mode**: placeholder screen before the movie and demo clip before the screening, chosen by the user; the background can come from TMDb.

## 9. Network: DLNA, Jellyfin, Emby

- **DLNA**: automatic server discovery, browsing and playback.
- **Jellyfin**: network discovery, login with username and password or with Quick Connect, catalog with the server's metadata, playback of the original file, resume point and "watched" status synced with other devices.
- **Emby (3.6)**: same features as Jellyfin except Quick Connect, which Emby doesn't have.
- **Reduced quality**: with a bitrate limit the server converts the video on the fly; applies to both Jellyfin and Emby.
- **Access tokens** saved encrypted and never written to logs.

## 10. Music

- **Cinecore Audio Engine**: dedicated engine for music.
  - WASAPI output: shared, exclusive or bit-perfect.
  - 10- or 31-band graphic equalizer or parametric, with presets.
  - Clipping protection (headroom or limiter) with adjustable ceiling.
  - Crossfeed for headphones, stereo width, balance, mono, loudness.
  - ReplayGain per track or per album, with preamp.
- **Crossfade between tracks** (3, 6 or 10 seconds), excluded between consecutive tracks of the same album.
- **Gapless (3.6)**: consecutive tracks join without a pause, even in exclusive and bit-perfect modes.
- **Music area**: cover, artist photo, queue, favorites, mini player.
- **Lyrics**: automatic search (LRCLIB, Genius, lyrics.ovh), synced lyrics when available, automatic synchronization with audio recognition if the optional module is installed.
- **Artist photos and covers**: from the disc, then from Spotify, TIDAL or Deezer (images and metadata only, no streaming).
- **Radio**: at the end of the queue, adds similar tracks from your own library, without external services.
- **Repeat track**, shuffle, remembered album progress.
- **Scrobbling to Last.fm and ListenBrainz (3.6)**: submission of listened tracks, with a queue for when the network is unavailable.

## 11. Photos

- **Viewer**: zoom, rotation, slideshow, first and last shot.
- **Editing**: pen, highlighter, line, arrow, rectangle, ellipse, text, filters, color picked from the photo, undo; always saves a copy.
- **Actions**: copy to clipboard, share with the Windows panel, print, open with another app, move to the Recycle Bin.

## 12. HDR analysis

- **Declared metadata**: mastering display, MaxCLL, MaxFALL, Dolby Vision, HDR10+.
- **Real measurement on the movie**: peak and average in nits over the whole duration, percentiles, brightness distribution, how much of the image falls outside Rec.709 and DCI-P3.
- **Real time**: RGB waveform, vectorscope, CIE 1931 chromaticity, false colors.
- **Export** of the analysis to file.

## 13. Phone remote

- **Web page served by the player**: pair by scanning a QR code, with nothing to install; can be added to the phone's Home screen.
- **Controls**: play, pause, stop, skips, fast scan, chapters, volume, full screen, arrows and OK, keyboard for typing on the PC.
- **Tracks**: audio and subtitles.
- **Library and queue** from the phone, with a choice between PC and network server.
- **Settings (reorganized in 3.6)**: Playback, Display, Player, Advanced components, Remote. They include renderer, HDR, 3D, upscaling, Jellyfin/Emby quality, full-screen start, theme, language and the components' full pages.
- **Customization (3.6)**: order and presence of blocks, color, home page, haptic feedback on touch.
- **Pairing prompt** at the player's first launch.

## 14. Home devices

- **Network amplifier**: receiver volume and mute from the player, with safety limits (never more than 3 dB per command, maximum ceiling, start at the last level used).
- **WLED lights**: turn off or dim during playback.
- **Automations**: at start, pause, resume, stop and end credits the player can send an HTTP request (GET or POST) or an MQTT message, for example to Home Assistant.

## 15. Connected services and updates

- **Trakt**: code-based linking, personal recommendations, submission of what you've watched and your ratings.
- **Player update** from GitHub releases, with installer fingerprint verification; optional check at startup.
- **External components**: versions and updates of yt-dlp, LAV Filters and MakeMKV from within the player.
- **Start with Windows**, optional.
- **Italian and English** across the whole interface.
- **Keys and tokens**: never in plain text in files or logs. The program contains only the application's own keys (Trakt and Last.fm); those tied to a personal account (TMDb, Subdl, OpenSubtitles, Spotify, TIDAL) are entered by each user and remain in their Windows profile, encrypted.

## 16. Keyboard shortcuts

| Key | Action |
|---|---|
| Space | Play / pause (in photos: slideshow) |
| ← → | Back / forward 10 seconds (in photos: previous / next) |
| `,` `.` | Previous / next frame |
| PgUp PgDn | Next / previous chapter |
| ↑ ↓ | Volume, and above 100% amplification |
| F | Full screen |
| Shift+F | Extended full screen |
| Z | Image sizing |
| Ctrl + / Ctrl − | Audio delay of 10 ms (with Shift: 100 ms) |
| Ctrl 0 | Reset audio delay |
| O | Open file |
| S | Close and return to the library |
| Esc | Back / exit full screen |
| In photos: + − 0 R | Zoom, initial view, rotation |
| In photos: Home / End | First / last photo |
