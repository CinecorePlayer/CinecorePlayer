# Cinecore Player: Third-Party Credits and Licenses

Reference version: **Windows Beta 4.1 (0.4.1)**, November 2026.

Cinecore Player includes or distributes the components listed below. Each third-party component remains subject to its own license and to the terms given by its authors. The PolyForm Noncommercial 1.0.0 license applies only to Cinecore's original code; it does not replace the licenses of these components.

None of these components is in the source repository: they are binaries of other projects (see [`third-parties/README.md`](third-parties/README.md)). No decryption keys or KEYDB files are included in any package.

## Bundled programs and runtimes

| Component | Version / build | Credit and license |
|---|---|---|
| .NET Desktop Runtime | 9.0, x64, self-contained (app, VideoHost, IntroOutroDetector) | Microsoft; MIT, notices in the runtime files. [.NET](https://dotnet.microsoft.com/) |
| FFmpeg | 7.1.1 full build, gyan.dev | FFmpeg developers; built with `--enable-gpl --enable-version3`, GPL-3.0-or-later. [FFmpeg legal](https://ffmpeg.org/legal.html) |
| libmpv | v0.41.0-690-g1ac687d79, x86-64 and x86-64-v3 | mpv project; GPL-2.0-or-later. [mpv](https://mpv.io/) |
| yt-dlp | 2026.08.19 | yt-dlp contributors; [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE) |
| Rubber Band Library | 4.0.0, built from the official sources (MSVC 2022, x64) | Breakfast Quay; GPL-2.0-or-later, text in `third-parties/rubberband/COPYING.txt`. Used for Automix time-stretching. [Rubber Band](https://breakfastquay.com/rubberband/) |
| madVR | 0.92.17 | madshi; terms in `third-parties/madVR09217/license.txt`. Original files and license kept as distributed. [madVR](https://www.videohelp.com/software/madVR) |
| MPC Video Renderer | 0.9.7.2387 | MPC Video Renderer contributors (Aleksoid1978); GPL-3.0, text in `third-parties/MpcVideoRenderer-0.9.7.2387/LICENSE.txt`. [Project](https://github.com/Aleksoid1978/VideoRenderer) |
| XySubFilter | 3.1.0.752 x64 | xy-VSFilter / XySubFilter developers; GPL-2.0-or-later. Original files and readme in `third-parties/XySubFilter_3.1.0.752_x64`. [Project](https://github.com/Cyberbeing/xy-VSFilter) |
| libaacs, libbdplus | 0.12.0 and 0.2.0, MSYS2 ucrt64 builds | VideoLAN; LGPL-2.1-or-later, text in `third-parties/libaacs/licenses`. Libraries only. [libaacs](https://www.videolan.org/developers/libaacs.html) |
| libgcrypt, libgpg-error | 1.12.4 and 1.61, MSYS2 ucrt64 builds | g10 Code; LGPL-2.1-or-later, text in `third-parties/libaacs/licenses`. Dependencies of libaacs. |

## Optional components (original installers)

Offered at the end of setup, selectable one by one, and launched only with the user's consent.

| Component | Version | Credit and license |
|---|---|---|
| LAV Filters | 0.80 | Hendrik Leppkes; GPL-2.0-or-later. [Project](https://github.com/Nevcairiel/LAVFilters) |
| MPC Audio Decoder | 1.8.5.59 x64 | MPC-BE / MPC-HC developers; GPL. Terms shown by the installer. |
| MPC Audio Renderer | 1.8.4.132 x64 | MPC-BE / MPC-HC developers; GPL. Terms shown by the installer. |

## Not included

| Component | Notes |
|---|---|
| Microsoft Edge WebView2 Runtime | Evergreen; setup downloads Microsoft's bootstrapper only if it is missing and the user agrees. [Distribution](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution) |
| MakeMKV | GuinpinSoft inc. If the user has installed it, Cinecore uses its `libmmbd64.dll` in place. |
| Python + faster-whisper | Optional, installed by the user for lyrics sync. |

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) (Jordan Russell and Martijn Laan; Inno Setup License).

## .NET / NuGet packages

Versions as restored for this build. Build-only packages (e.g. `Avalonia.BuildServices`) are not shipped.

| Package | Version | License | Used by |
|---|---:|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Skia, Avalonia.Themes.Fluent, Avalonia.Win32, Avalonia.Native, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.Remote.Protocol | 11.3.0 | MIT | App — [Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| Avalonia.Angle.Windows.Natives | 2.1.22045.20230930 | BSD-3-Clause (ANGLE) | App |
| SkiaSharp (+ NativeAssets) | 2.88.9 | MIT | App — [SkiaSharp](https://github.com/mono/SkiaSharp) |
| HarfBuzzSharp (+ NativeAssets) | 7.3.0.3 | MIT | App |
| MicroCom.Runtime | 0.11.0 | MIT | App |
| Tmds.DBus.Protocol | 0.21.2 | MIT | App (Avalonia dependency) |
| NAudio, NAudio.Core, NAudio.Wasapi, NAudio.WinMM, NAudio.Asio, NAudio.Midi, NAudio.WinForms | 2.2.1 | MIT | App — [NAudio](https://github.com/naudio/NAudio) |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Microsoft WebView2 SDK license | App — [WebView2](https://aka.ms/webview) |
| System.Security.Cryptography.ProtectedData | 9.0.0 | MIT | App |
| System.IO.Pipelines | 8.0.0 | MIT | App |
| Microsoft.Win32.Registry, System.Security.AccessControl, System.Security.Principal.Windows | 4.7.0 | MIT | App |
| Microsoft.NETCore.Platforms | 3.1.0 | MIT | App |
| DirectShowLib | 1.0.0 | LGPL-2.1 | VideoHost — [DirectShowLib](https://sourceforge.net/projects/directshownet/) |
| Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX | 3.6.2 | MIT | VideoHost — [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) |
| Vortice.Mathematics | 1.9.2 | MIT | VideoHost |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.2.0-beta | MIT | VideoHost — [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools) |

## Fonts

Inter and Inter Display (Rasmus Andersson), Manrope (Mikhail Sharanda) and Rubik (Hubert and Fischer, Google Fonts) in `Assets/Fonts`, under the [SIL Open Font License 1.1](https://openfontlicense.org/). Projects: [Inter](https://github.com/rsms/inter), [Manrope](https://github.com/sharanda/manrope), [Rubik](https://github.com/googlefonts/rubik).

## Icons

| Folder | Source | License |
|---|---|---|
| `Assets/Icons.Uniform` | [Tabler Icons](https://github.com/tabler/tabler-icons) (Paweł Kuna) | MIT |
| `Assets/Icons.Lucide` | [Lucide](https://lucide.dev/), plus Cinecore icons drawn in the same style | ISC (Lucide; parts derived from Feather, MIT) |
| `Assets/hud_icons`, `Assets/hud_icons_new` | [SVG Repo](https://www.svgrepo.com/) | Per icon, as stated on each icon's SVG Repo page |
| `Assets/Icons.Custom`, `Assets/icons` | Cinecore | PolyForm Noncommercial 1.0.0 |

## Cinecore license

Cinecore Player's original code is released under the [PolyForm Noncommercial License 1.0.0](LICENSE); the text is included in the application folder.
