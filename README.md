# 🎬 Cinecore Player 2026

[![License: PolyForm Noncommercial 1.0.0](https://img.shields.io/badge/License-PolyForm%20Noncommercial%201.0.0-blue.svg)](https://polyformproject.org/licenses/noncommercial/1.0.0)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements)
[![Status](https://img.shields.io/badge/status-alpha-orange)](#-project-status)
[![Downloads](https://img.shields.io/github/downloads-pre/NicoLando024/CinecorePlayer/total.svg)](https://github.com/NicoLando024/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/NicoLando024/CinecorePlayer?style=flat&logo=github)](https://github.com/NicoLando024/CinecorePlayer)
[![Alpha Available](https://img.shields.io/badge/alpha-download%20now-brightgreen?logo=github)](https://github.com/NicoLando024/CinecorePlayer/releases/tag/alpha1)
[![Languages](https://img.shields.io/badge/languages-English%20%7C%20Italian-4C9EEB)](#-localization)

Cinecore Player is a **free**, **source-available**, and **non-commercial** media player for Windows, built in **C# / .NET 9.0** and focused on high-quality playback with **madVR**.

Designed as a modern DirectShow-based player, Cinecore combines advanced video rendering, intelligent **HDR management**, and support for multiple renderer backends, including **madVR**, **MPC Video Renderer (MPCVR)**, **EVR**, and **libmpv**.

Beyond playback, Cinecore includes a growing set of media-center features such as a **TMDB-integrated library**, an **online remote controller**, **Cinema Mode**, resume playback, playlist and favorites support, and many other interface and usability improvements.

The player is also built with **audiophiles** in mind, featuring a dedicated **Audio Mode** with real-time visualizations such as an oscilloscope and spectrum analyzer.

Audio output supports both **bitstream** and **PCM**, with compatibility for **exclusive** and **non-exclusive** output modes.

> **Development status:** Cinecore Player is currently in active development.  
> A public alpha is already available, while **Beta V1 is currently in development**, focusing on bug fixes, UI improvements, new features, and overall refinement.

---

## 📸 Screenshots

> ### 🚧 Beta V1 Interface
> The screenshots below show the upcoming **Cinecore Beta V1** interface, including many features and visual changes that are **not yet available in the current public alpha**.

### 🏠 Home

![Cinecore Player Home](Screenshots/Screenshot%202026-09-02%20114007.png)

---

### 🎞️ Library

![Cinecore Player Library](Screenshots/Screenshot%202026-09-02%20114031.png)

---

### 🎬 Film Details

![Cinecore Player Film Details](Screenshots/Screenshot%202026-09-02%20114246.png)

---

### ▶️ Video Player

![Cinecore Player Video Player](Screenshots/Screenshot%202026-09-02%20123048.png)

---

### 🎵 Audio Graphs

![Cinecore Player Audio Graphs](Screenshots/Screenshot%202026-09-02%20120339.png)

---

### ℹ️ Info Overlay

![Cinecore Player Info Overlay](Screenshots/Screenshot%202026-09-02%20123115.png)

---

### 📡 DLNA

![Cinecore Player DLNA](Screenshots/Screenshot%202026-09-02%20114109.png)

---

### 📺 YouTube

![Cinecore Player YouTube](Screenshots/Screenshot%202026-09-02%20114131.png)

---

## 🌍 Localization

Cinecore Player currently supports two interface languages:

- 🇬🇧 **English**
- 🇮🇹 **Italian**

The interface language can be changed directly from the application settings.

---

## 📌 Project Status

Cinecore Player is currently in active development.

The latest publicly available release is an **alpha**, while development has now moved toward **Beta V1**.

The core playback experience is already usable, and several advanced features have been implemented. However, some modules are still incomplete, experimental, or not yet fully polished.

Development is currently focused on improving stability, expanding renderer support, refining the new HUD, improving audio and video handling, and introducing additional media-center functionality.

---

## 🖥️ System Requirements

### End Users

- **Operating System:** Windows
- **Runtime:** .NET 9.0
- **Supported Video Renderers:**
  - madVR
  - MPC Video Renderer (MPCVR)
  - EVR
  - libmpv

> Some advanced features may depend on the selected renderer and system configuration.

---

## ✅ Working Features

### 🎥 Video Playback

Video playback is supported through:

- **madVR**
- **MPC Video Renderer (MPCVR)**
- **EVR**
- **libmpv**

Core playback is functional and generally stable across supported renderers.

---

### 🔊 Audio Playback

Cinecore supports:

- **PCM audio**
- **Bitstream audio**
- **Exclusive output**
- **Non-exclusive output**

Standard playback works reliably across supported formats and output configurations.

---

### 🎞️ TMDB-Integrated Library

The media library includes **TMDB integration** for retrieving movie and TV-series metadata.

---

### 📱 Online Remote Control

Cinecore includes an online remote-control interface that can be used during playback.

---

### 🍿 Cinema Mode

Cinema Mode provides a more immersive pre-movie experience.

It currently includes:

- Pre-playback movie placeholder screen
- **Dolby Atmos** demo playback
- **DTS:X / DTS-HD MA** demo playback
- **THX** demo playback
- **WLED integration**
- Automatic room-light control
- Automatic transition from demos to movie playback

---

### ⏯️ Resume Playback

Movies and other media can be resumed from the point where playback was previously stopped.

---

### 📊 Audio Graphs

Real-time audio visualization is functional.

Current visualizations include:

- Oscilloscope
- Spectrum analyzer
- Additional visualization modes

---

### 🎤 Lyrics

Lyrics integration is generally functional.

---

### 🖼️ Photo Viewer

The integrated photo viewer is fully operational.

---

### 🖥️ HUD / On-Screen Display

The Cinecore HUD is generally functional and provides playback information and controls directly over video content.

A completely redesigned HUD is being introduced with **Beta V1**.

---

### ⏭️ Skip Intro / Outro

Skip Intro / Outro functionality is available for TV series, including support for automatically moving to the next episode.

---

### 📡 DLNA

DLNA functionality is implemented and generally operational.

---

## ⚠️ Known Issues

The currently available public version of Cinecore Player is still an **alpha release**, so bugs and incomplete functionality should be expected.

Current known issues include:

- Occasional **HUD graphical glitches**
- Some UI elements still require refinement
- Certain features may behave differently depending on the selected renderer
- Some modules remain experimental
- General stability and usability bugs are still being investigated

Bug reports and feedback are welcome.

---

## 🛠️ In Development

The following features and improvements are currently being developed for **Beta V1** and future updates:

- **PCM audio improvements**
- **Real-time HDR analyzer**
- **HUD personalization options**
- **360° video rendering**
- **YouTube integration**
- **Expanded renderer settings**
- **Additional HUD refinements**
- **Playback improvements**
- **New media-center features**
- **General UI improvements**
- **Quality-of-life improvements**
- **Bug fixes and stability improvements**
- **Automatic audio/video sync detection and correction**

Many additional features are also being tested or planned for future versions.

---

## 🚧 Beta V1

**Cinecore Player Beta V1** is currently in active development.

Beta V1 represents a major evolution over the current public alpha and focuses primarily on:

- A redesigned HUD
- Major UI improvements
- Better playback stability
- Expanded renderer configuration
- Audio improvements
- Playback refinements
- Bug fixes
- New media-center functionality
- General quality-of-life improvements

The screenshots shown above represent the current direction of **Beta V1** and may still differ from the final public release.

---

## 💡 Suggestions & Feedback

Every suggestion is appreciated.

If you find a bug, have an idea for a new feature, or want to suggest an improvement, feel free to open an **Issue** on GitHub.

Feedback is especially useful during the current development stage.

---

## 📥 Download

The latest public alpha can be downloaded from the GitHub Releases page:

**[Download Cinecore Player Alpha](https://github.com/NicoLando024/CinecorePlayer/releases/tag/alpha1)**

---

## 📄 License

Cinecore Player is distributed under the **PolyForm Noncommercial 1.0.0 License**.

The project is source-available and may be used, modified, and studied under the conditions defined by the license.

Commercial use is not permitted.

For more information, see the [`LICENSE`](LICENSE) file included in this repository.

---

## ⭐ Support the Project

If you like Cinecore Player, consider leaving a **⭐ Star** on the repository.

It helps the project grow and makes it easier for other users to discover it.

---

**Cinecore Player**  
*A modern Windows media player focused on playback quality, customization, and the home-cinema experience.*
