# Cinecore Player Beta 1 for macOS (0.1.0)

The first macOS version of Cinecore Player, for **Apple Silicon**. It has the same Avalonia interface and the same features as Windows Beta 4, with native mpv for video and music, and **madVR** and **MPC Video Renderer** through an engine that the app downloads by itself.

## Install

Download `CinecorePlayer2026-Mac-0.1.0-arm64.dmg` below, open it and drag **CinecorePlayer2026** onto *Applicazioni* (Applications). That is all: on the first launch the app downloads the rest in the background, straight from the official sites, like the components already ticked in the Windows installer:

- **MetalSharp Wine 6.0** for ARM64, from the v6.0 release of [metalsharp/MetalSharp-Wine](https://github.com/metalsharp/MetalSharp-Wine);
- **madVR** from madshi.net, **MPC Video Renderer**, **LAV Filters** and **XySubFilter** from their authors' GitHub releases.

Every archive and every file taken from it is checked against the SHA-256 written inside the app. Cinecore's own parts — the Wine libraries rebuilt with our patches for madVR and MPC Video Renderer, the DirectShow library, the tuned settings of madVR, LAV, MPC Video Renderer and XySubFilter, and the VideoToolbox bridge — are already inside the app. Wine then creates its Windows folder on your Mac and the filters are registered, exactly as in the engine used for testing. Progress, and *Cancel*, are in *Settings › Updates › madVR and MPC Video Renderer engine*. Without the engine everything plays with mpv.

The `.zip` next to the `.dmg` is the same app, used by the built-in updater.

> [!IMPORTANT]
> **System requirements.** macOS 13 or later on an Apple Silicon Mac (M1 or later). Intel Macs are not supported.
>
> **First launch.** The app is not notarized by Apple: open it with right-click › *Open*, or allow it in *System Settings › Privacy & Security*.
>
> **Disk space.** The engine needs about 1.4 GB to download and 6.8 GB once installed, in `~/Library/Application Support/Cinecore/engine`.
>
> **madVR and MPC Video Renderer** run through Wine for ARM64. They are stable, but not as efficient as mpv, which is native.
>
> **TMDb key.** Artwork, plots, cast and directors need your own free TMDb key, entered once in *Settings*.

## New

### Playback

- **Same features as Windows.** Library, Spotlight, detail sheets with reviews and cast, playback overlay, Info panel, subtitles with search and re-timing, audio sync, HDR and SDR analysis, Jellyfin, Emby, DLNA and Plex, Trakt, scrobbling, phone remote, Cinema Mode, WLED and home automations.
- **mpv native**, with VideoToolbox decoding and HDR shown on the Mac display (EDR).
- **madVR and MPC Video Renderer** with LAV Filters and XySubFilter, in the downloadable engine.
- **Exact presentation.** With madVR and MPC Video Renderer each frame is shown at the vsync it belongs to, on the screen the film is on, also on an external display or projector at 23.976 / 24 Hz.
- **Refresh-rate switching** before the film starts, also when the display only offers 24.000 Hz for a 23.976 film.
- **Bitstream** of Dolby Digital, Dolby Digital Plus (also with Atmos) and plain DTS to an HDMI receiver, chosen per track: music in PCM or bit-perfect, a Dolby or DTS song in bitstream. DTS-HD and DTS:X are decoded (macOS does not offer them over HDMI); TrueHD is decoded unless the HDMI adapter carries HBR.
- **Changing the audio device** or *Auto / Force PCM* during a film rebuilds only the audio: the renderer stays on.
- **Changing the quality** of a YouTube, DLNA or Jellyfin stream reloads it in place, from the same point, with a spinner — no full reload.
- **DVDs with their menus** on every renderer, **audio CDs** recognised through MusicBrainz, Blu-ray main title.
- **Volume above 100%** with every renderer.
- **Network receiver volume** (Denon, Marantz and others over IP) in 1 dB steps: the slider stays where you leave it while the receiver gets there, raising at most 3 dB per command and never above −20 dB.
- **HDR and SDR analysis** is muted and lets you choose how many frames per second to measure live (1 to 24).
- **Info panel** shows the live video and audio bitrate, also for YouTube and DLNA streams.
- Media keys and *Now Playing* of macOS.

### Interface

- **Liquid Glass** on macOS 26 and later for the music bar, the queue, the menus and the photo controls; a real blur on earlier versions.
- New Home with a rotating hero, new playback overlay, redesigned Photos and photo editor, Trakt "Recommended for you" with *See all*, *Mark as watched* and *Not interested* (see the Windows Beta 4 notes).
- **Phone remote** with redesigned settings: grouped categories, search, and the same options as the Mac's Settings window (playback, picture, subtitles, music, screens, library, updates), plus a *This Mac* monitor. The **trackpad** moves the Mac's pointer, clicks and scrolls; the first time, macOS asks to allow Cinecore in *Privacy & Security › Accessibility*.
- **Spotlight** fully usable from the remote: OK opens the film, up reaches search, favourites and settings, down goes back. Right-click menu and the settings menu work there too.
- **Playlists** of music open like an album, with a cover made of the first four albums when you have not chosen one.
- **Continue listening** shows the track you stopped at and how much of the album is done.
- YouTube shows its thumbnail while the video is being resolved.
- English, Italian and Spanish; TMDb data in the chosen language.

### Music

- **Cinecore Audio Engine for Mac** on Core Audio: equalizer (10 bands, 31 bands, parametric), ReplayGain, crossfeed, limiter, **gapless**, crossfade, **exclusive / bit-perfect** output.
- **Automix**, **Tempo sync** and the **manual mix editor**, as on Windows. In the manual mix you can also change the speed of the outgoing track: it eases into the new tempo at the start of the transition, and the incoming track returns smoothly to its own tempo afterwards.
- **Synced lyrics** are aligned to the voice more reliably (repeated choruses, openings sung twice); *Sync lyrics* is in the ••• menu of the music bar, with a small progress line above the lyrics.
- Full-screen track view, synced lyrics (also aligned to the audio), mini player, radio, charts and VU meters.

### Mac integration

- Photos, videos, music, playlists (`.wpl`) and discs open from the Finder straight into the right place.
- Share sheet, copy, Trash, *Show in Finder*, *Open with*, set as desktop picture, places in Maps.
- *Open at login*, drag and drop of files on the window.
- Keys, tokens and passwords stored encrypted, never in clear text.
- Updates from the GitHub releases, like on Windows.

## Notes

- **madVR presets.** During testing on an M4 MacBook Air in Low Power Mode, playback struggled with the heaviest madVR presets on 4K 16:9 films (the GPU is held at low clocks). Wider films and lighter presets keep up. When it happens, the player offers to switch to MPV or MPC Video Renderer for that session; it never switches by itself.
- With madVR's statistics open (Ctrl+J) a few more frames can be dropped.
- 3D side-by-side and top/bottom → 2D work with madVR too (fixed 21/11: the VideoHost crashed asking madVR for its aspect ratio).
- Live HDR analysis at 24 frames per second reaches about 13–15 measurements per second; drawing the scopes is the limit.
- Blu-ray playback is in place but has not been tested on a real disc yet.
