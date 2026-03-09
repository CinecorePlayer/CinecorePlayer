# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#️-system-requirements-end-users)
[![Status](https://img.shields.io/badge/status-alpha-orange)](#-project-status-truthful-current)
[![Build](https://img.shields.io/github/actions/workflow/status/NicoLando024/CinecorePlayer/build.yml?branch=main)](https://github.com/NicoLando024/CinecorePlayer/actions)
[![Downloads](https://img.shields.io/github/downloads/NicoLando024/CinecorePlayer/total.svg)](https://github.com/NicoLando024/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/NicoLando024/CinecorePlayer.svg?style=social&label=Star)](https://github.com/NicoLando024/CinecorePlayer)

A **free**, **non-commercial** media player for **Windows**, built in **C# / .NET 9.0** and powered by **MadVR**.  
It features intelligent **HDR management** and supports multiple high-end **video renderer backends**, including **madVR**, **MPC Video Renderer (MPCVR)**, and **EVR**.

The player also includes a **TMDB-integrated library**, an **online remote controller**, a dedicated **Cinema Mode**, and **resume playback** functionality to continue content from where it was left off.

Designed with **audiophiles** in mind, Cinecore Player includes a dedicated **Audio Mode** with real-time visualizations such as **oscilloscope** and **spectrum analyzer**.  
Audio output supports both **bitstream** and **PCM**, with compatibility for **exclusive** and **non-exclusive** modes.

---

## 📸 Screenshots

![Home](Screenshots/home.png)

![Library](Screenshots/library.png)

![Audio Graphs](Screenshots/audio-graphs.png)

![Video Player](Screenshots/video-player.png)

![Info Overlay](Screenshots/info-overlay.png)

---

## 📌 Project status (truthful current)

Cinecore Player is currently in **alpha**.  
The core playback experience is already usable, and several advanced features are implemented, but some modules are still incomplete, experimental, or not yet fully polished.

---

## 🖥️ System requirements (end users)

- **Operating system:** Windows
- **Runtime:** .NET 9.0
- **Renderer support:** madVR / EVR / MPCVR backend support is present in the player, although not all backends are currently equally functional

---

## ✅ Working features

- **Video playback via madVR and EVR**  
  Core playback is stable on these renderers.

- **Audio playback (PCM & Bitstream)**  
  Standard playback works reliably across supported formats.

- **TMDB-integrated library**  
  The media library includes TMDB integration.

- **Online Remote Control**  
  Remote control functionality is implemented and usable during playback.

- **Cinema Mode**  
  Includes a pre-playback movie placeholder screen, demo playback for **Dolby Atmos / DTS:X MA / THX**, **WLED** integration for turning off room lighting, and then movie playback.

- **Resume playback**  
  Content can be resumed from the point where playback was previously stopped.

- **Audio graphs (partial implementation)**  
  Audio analysis graphs are currently functional only with **non-exclusive PCM**.

- **Photo viewer**  
  Fully operational, with future quality-of-life improvements planned.

- **HUD / On-Screen Display**  
  Generally functional, though still in need of refinement.

---

## ⚠️ Known issues

- **Renderer settings not yet integrated**  
  Player-side configuration panels are still incomplete. Users must currently adjust settings directly inside each renderer.

- **HUD responsiveness and visual glitches**  
  The HUD works, but responsiveness is not yet ideal and occasional graphical artifacts may still occur.

- **Audio graphs in exclusive PCM mode**  
  In audio-only playback, graphs currently work only in **non-exclusive PCM** mode.

- **DLNA module**  
  The DLNA implementation is still highly incomplete and may be unstable or error-prone.

- **MPCVR backend currently not working**  
  MPC Video Renderer support is present at project level, but it is **not currently functional**.

- **Info currently not working**  
  Info is present at project level, but it is **currently partially functional**.

---

## 🚧 Current limitations

- **3D-to-2D conversion**  
  Currently functional only with **EVR**. **madVR** support is planned.

- **Player settings coverage**  
  Several configuration sections are only partially implemented.

---

## 🛠️ In development

- **Favorites**
- **Playlists**
- **YouTube integration**
- **DLNA improvements**
- **Expanded renderer settings**
- **Additional HUD refinements**
- **English localization**
- **General QoL enhancements across the UI**

Many other additions are currently in development.

---

## 📄 License

This project is released under the  
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International License**.

You are free to:
- Share and adapt the material

Under the following terms:
- **Attribution** — appropriate credit must be given
- **NonCommercial** — no commercial use is allowed
- **ShareAlike** — derivatives must be distributed under the same license

Full license text:  
https://creativecommons.org/licenses/by-nc-sa/4.0/
