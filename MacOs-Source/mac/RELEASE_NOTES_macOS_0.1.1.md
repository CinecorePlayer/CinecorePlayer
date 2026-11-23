# Cinecore Player Beta 1.1 for macOS (0.1.1)

An update to Beta 1 for **Apple Silicon**: renderer presets with an automatic mode, the optimisations and fixes from the Windows version, a phone remote with the new settings layout, and complete English and Spanish translations.

## Install

Download `CinecorePlayer2026-Mac-0.1.1-arm64.dmg` below, open it and drag **CinecorePlayer2026** onto *Applications*. If Beta 1 is already installed, the built-in updater uses the `.zip` next to the `.dmg`. The madVR and MPC Video Renderer engine is unchanged: if it is already installed, nothing is downloaded again.

> [!IMPORTANT]
> macOS 13 or later on an Apple Silicon Mac (M1 or later). The app is not notarized by Apple: open it with right-click › *Open* the first time, or allow it in *System Settings › Privacy & Security*.

## New

### Renderer presets

- **Video › Presets** in the right-click menu:
  - **Automatic** (the default): the player decides by itself how hard the renderer works for the film that is starting. The level it chose is shown in grey next to the item.
  - **Custom**: uses your own automations.
  - **Turn off presets**: only your Settings apply.
  - The presets of the current renderer, chosen by hand, and *Manage presets…*.

  Choosing an item restarts the film from the same point. Automatic, Custom and Turn off are remembered across launches.
- **How Automatic decides.** It looks at:
  - the renderer (madVR costs far more than MPC Video Renderer and MPV);
  - the Mac: chip, GPU cores, and whether it has a fan;
  - Low Power Mode, battery, and how hot the Mac is (when it runs hot, it lowers the level by itself);
  - the film: resolution compared with its size on screen, frame rate, and source quality, measured from the bitrate per pixel and corrected for the codec.

  When a film has to be enlarged, the power goes first to upscaling:
  - low-quality sources (DVDs, low resolution or low bitrate): NGU Anti-Alias at the highest quality the Mac can sustain, plus banding, ringing and blocking clean-up;
  - good sources: NGU Sharp, then NGU chroma and SuperChromaRes if there is power left.

  4K films in a smaller window use the Low…Ultra levels.
- **Settings › Components › Presets**, a new page:
  - **automations** like on the iPhone ("When the film is 4K and Low Power Mode is on" → "Use madVR: High");
  - **presets** for madVR, MPC Video Renderer and MPV, where every setting starts from "As in Settings".

  A preset never changes your saved Settings.
- **Ready-made presets** for each renderer: Low, Medium, High, Ultra and "DVD and low quality". They can be edited or deleted.
- **madVR, new Performance tab**: madVR's quality/performance trade-offs, also usable in presets.
- New icons in the presets menu: a gauge whose needle rises from Low to Ultra.

### Faster interface (from the Windows version)

- Text is laid out once instead of on every frame.
- The library, Settings and sheets redraw only when the pointer enters or leaves an element, not on every mouse movement.
- The Home hero is composed into a single image.
- The music bar and the "now playing" bars have their own layer: with music playing the library goes from dozens of redraws per second to 2.
- Full-screen lyrics redraw only while they move.
- Fewer timers running in the background, and listening history loaded in the background at launch.
- The right-click menu is quicker: submenus open after 120 ms instead of 240, and building the menu takes about 1 ms.

### Playback

- **madVR** starts with its render queue full: no dropped frames at the start or after seeking (*Components › madVR › Performance*: "Start with a full queue", "After seeking too"). MPV starts once the first frame is ready.
- If madVR keeps dropping frames, the player offers to switch to a lighter engine and resumes from the same point.
- The **Low Power Mode warning** has a *Don't show again* option, no longer appears when a preset is already active, and is no longer drawn over the right-click menu.
- **HDR and SDR analysis** has the same layout as on Windows ("24 fps" menu, no progress bar under the menus) and more regular live measurements.

### Music

- **Automix** redesigned: a "Crossfade only" option, and no club effects on music that isn't dance music.
- **Manual mix**:
  - the incoming track follows the tempo of the outgoing one, even when that one is slowed down;
  - you can **draw volume, EQ and filter curves by hand**;
  - the music chart numbers change less nervously.

### Phone remote

- **Settings** with the new layout: *Player* and *Components* menus, sections with uppercase titles, drop-down choices. The settings are the Mac's own (no Windows-only options), and the page says "Mac" instead of "PC".
- The QR code no longer disappears when a phone that is already paired reconnects.

### Other

- **Colour picker**: the hex code is fully selected on the first click, Cmd+V pastes, Delete clears. It is drawn over a dark vignette instead of a card, and the ready-made colours match the row (accent or background).
- **TMDb** follows the app language and updates without starting over.
- The video **picture-in-picture** mode has been removed (the music mini player stays): with madVR on Windows the video froze.
- **Translations**: English and Spanish checked across the whole app, including every right-click menu item. The dictionary is now shared with the Windows version, with about 190 new entries.

## Notes

- Presets and Automatic are tuned on an M4 MacBook Air. Full-power limits are estimates; Low Power Mode limits are measured.
- The madVR / MPC Video Renderer engine (Wine, VideoHost) is unchanged in this update.

## SHA-256

| File | SHA-256 |
|---|---|
| `CinecorePlayer2026-Mac-0.1.1-arm64.dmg` | `1af096342ed5c624a1fa021c228afdad75ebec098161a4cf021ee89e9f8c36d9` |
| `CinecorePlayer-Mac-0.1.1-arm64.zip` | `97a5ee474ed1cf1582091f778ab6aeb4c18ebcc8a5aa299f947f80986dc52df7` |
