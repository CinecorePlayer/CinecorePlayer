# 🎬 Cinecore Player 2025

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#️-system-requirements-end-users)
[![Status](https://img.shields.io/badge/status-alpha-orange)](#-project-status-truthful-current)
[![Build](https://img.shields.io/github/actions/workflow/status/NicoLando024/CinecorePlayer/build.yml?branch=main)](https://github.com/NicoLando024/CinecorePlayer/actions)
[![Downloads](https://img.shields.io/github/downloads/NicoLando024/CinecorePlayer/total.svg)](https://github.com/NicoLando024/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/NicoLando024/CinecorePlayer.svg?style=social&label=Star)](https://github.com/NicoLando024/CinecorePlayer)

Cinecore Player is a **free**, **source-available**, **non-commercial** media player for Windows, built in **C# / .NET 9.0** and focused on high-quality playback with **madVR**.

Designed as a modern DirectShow-based player, Cinecore combines advanced video rendering, intelligent **HDR management**, and support for multiple renderer backends, including **madVR**, **MPC Video Renderer (MPCVR)**, **EVR** and **libmpv**.

Beyond playback, Cinecore includes a growing set of media-center features such as a **TMDB-integrated library**, an **online remote controller**, **Cinema Mode**, resume playback, playlist/favorites support, and many other interface and usability improvements.

The player is also built with **audiophiles** in mind, featuring a dedicated **Audio Mode** with real-time visualizations such as oscilloscope and spectrum analyzer. Audio output supports both **bitstream** and **PCM**, with compatibility for **exclusive** and **non-exclusive** output modes.

> **Development status:** Cinecore Player is currently in active development.  
> A public alpha has been published.

---

## 📸 Screenshots

## Home
![Home](Screenshots/home.png)

## Library
![Library](Screenshots/library.png)

## Audio Graphs
![Audio Graphs](Screenshots/graphs.png)

## Video Player
![Video Player](Screenshots/player.png)

## Info Overlay
![Info Overlay](Screenshots/info.png)

## Photo Player
![Photo Player](Screenshots/photo_player.png)

## DLNA
![DLNA](Screenshots/dlna.png)

## Settings
![Settings](Screenshots/settings.png)

## Remote
![Remote](Screenshots/remote.jpeg)

---

## 📌 Project status (truthful current)

Cinecore Player is currently in **alpha**.  
The core playback experience is already usable, and several advanced features are implemented, but some modules are still incomplete, experimental, or not yet fully polished.

---

## 🖥️ System requirements (end users)

- **Operating system:** Windows
- **Runtime:** .NET 9.0
- **Renderer support:** madVR / EVR / MPCVR and soon libmpv

---

## ✅ Working features

- **Video playback via madVR, mpcvr, EVR and libmpv**  
  Core playback is stable on these renderers.

- **Audio playback (PCM & Bitstream)**  
  Standard playback works reliably across supported formats.

- **TMDB-integrated library**  
  The media library includes TMDB integration.

- **Online Remote Control**  
  Remote control functionality is implemented and usable during playback.

- **Cinema Mode**  
  Includes a pre-playback movie placeholder screen, demo playback for **Dolby Atmos / DTS:X MA / THX**, **WLED** integration   for turning off room lighting, and then movie playback.

- **Resume playback**  
  Content can be resumed from the point where playback was previously stopped.

- **Audio graphs**
  Functional.

- **Lyrics**  
  Generally functional.

- **Photo viewer**  
  Fully operational.

- **HUD / On-Screen Display**  
  Generally functional, though still in need of refinement.

- **SKIP Intro/Outro (next episode) for TV Series**  
  Functional.

- **DLNA**  
  Generally functional.

---

## ⚠️ Known issues

- **Renderer settings not yet fully integrated**  
  Player-side configuration panels are still incomplete. Users must currently adjust settings directly inside each renderer.

- **HUD responsiveness and visual glitches**  
  The HUD works, but responsiveness is not yet ideal and occasional graphical artifacts may still occur.

- **YouTube integration not working**

- **MPCVR HUD not working**
  
---

## 🛠️ In development

- **PCM audio tweaks**
- **Realtime HDR analyzer**
- **HUD personalization options**
- **Netflix like HUD mode**
- **360 rendering**
- **YouTube integration**
- **Expanded renderer settings**
- **Additional HUD refinements**
- **New features**
- **General QoL enhancements across the UI**

Many other additions are currently in development.

## ANY SUGGESTION IS APPRECIATED

---
