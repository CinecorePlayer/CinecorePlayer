# Cinecore Player per macOS - crediti e licenze di terze parti

L'app Mac (CinecorePlayer2026.app) è scritta in .NET 9 con Avalonia. Include o scarica i componenti elencati qui sotto, che restano soggetti alle rispettive licenze e ai termini dei loro autori. La licenza PolyForm Noncommercial di Cinecore vale per il codice originale Cinecore e non sostituisce le licenze di questi componenti.

I componenti inclusi stanno in `Contents/MacOS` e `Contents/Frameworks` del pacchetto `.app`; gli altri l'app li scarica dai siti ufficiali degli autori e ne controlla l'impronta.

## Inclusi nel pacchetto

| Componente | Versione | Credito e licenza |
|---|---|---|
| .NET Runtime | 9.0, incluso nel publish autonomo | Microsoft e .NET Foundation; MIT. [dotnet/runtime](https://github.com/dotnet/runtime). |
| Avalonia | 11.3.0 (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Native) | AvaloniaUI OÜ e contributori; MIT. [Avalonia](https://github.com/AvaloniaUI/Avalonia). |
| SkiaSharp, HarfBuzzSharp | versioni portate da Avalonia 11.3.0 | Microsoft; MIT. |
| libmpv | 0.42.0, compilata da `mac/build-mpv-coreaudio.sh` (uscita CoreAudio con bitstream Dolby) | mpv project; GPL-2.0-or-later (la build usa FFmpeg GPL). [mpv](https://mpv.io/). Sorgente ufficiale più le patch in `mac/mpv-patches`. |
| FFmpeg (librerie, `ffmpeg`, `ffprobe`) | 9.0.2, build Homebrew con `--enable-gpl --enable-version3` | FFmpeg developers; GPL-3.0-or-later per questa configurazione. [FFmpeg legal](https://ffmpeg.org/legal.html). Patch Cinecore in `mac/ffmpeg-patches`. |
| libplacebo | 7.387.0 | VideoLAN; LGPL-2.1-or-later. |
| MoltenVK, Vulkan loader, shaderc | 1.4.2 / 1.4 | Khronos Group, The Brenwill Workshop, Google; Apache-2.0. |
| Rubber Band Library | 4.0.0 | Particular Programs Ltd; GPL-2.0-or-later. |
| libass, fribidi, harfbuzz, freetype, fontconfig, graphite2, libunibreak | build Homebrew | libass ISC; fribidi LGPL-2.1-or-later; HarfBuzz MIT; FreeType FTL; Fontconfig MIT; Graphite2 LGPL-2.1-or-later/MPL; libunibreak zlib. |
| libbluray, libudfread, libdvdnav, libdvdread, libdvdcss | build Homebrew | VideoLAN; libbluray e libudfread LGPL-2.1-or-later; libdvdnav, libdvdread e libdvdcss GPL-2.0-or-later. Nessuna chiave AACS inclusa: libaacs e `KEYDB.cfg` li fornisce l'utente. |
| dav1d, x264, x265, SVT-AV1, libvpx, libvmaf, opus, LAME, mpg123 | build Homebrew | dav1d BSD-2-Clause; x264 e x265 GPL-2.0-or-later; SVT-AV1 BSD-3-Clause-Clear; libvpx BSD-3-Clause; libvmaf BSD-2-Clause-Patent; Opus BSD-3-Clause; LAME e mpg123 LGPL-2.1-or-later. |
| zimg, Little CMS, uchardet, LuaJIT, MuJS | build Homebrew | zimg WTFPL; lcms2 MIT; uchardet MPL-1.1/GPL-2.0/LGPL-2.1; LuaJIT MIT; MuJS ISC. |
| OpenSSL, libarchive, zstd, lz4, xz (liblzma), brotli, libb2, libpng, libjpeg-turbo, PCRE2, GLib, gettext (libintl), libsamplerate | build Homebrew | OpenSSL Apache-2.0; libarchive BSD-2-Clause; zstd BSD-3-Clause; lz4 BSD-2-Clause; liblzma 0BSD; brotli MIT; libb2 CC0; libpng libpng License; libjpeg-turbo IJG/BSD-3-Clause; PCRE2 BSD-3-Clause; GLib e libintl LGPL-2.1-or-later; libsamplerate BSD-2-Clause. |
| Wine (file sostituiti: `wined3d`, `winemac`) | basati su MetalSharp Wine 6.0 | Wine project; LGPL-2.1-or-later. Le modifiche Cinecore sono in `mac/wine-patches`. |
| Ponte VideoToolbox (`CinecoreVideoToolbox.dll`, `cinecorevt.so`) | dal fork FFmpeg di LAV Filters, `mac/build-videotoolbox-bridge.sh` | Hendrik Leppkes e FFmpeg developers; LGPL-2.1-or-later. |
| VideoHost (processo dei renderer madVR e MPC-VR sotto Wine) | DirectShowLib 1.0.0, Vortice.Direct3D11 3.6.2 | DirectShowLib LGPL-2.1; Vortice.Windows (Amer Koleci) MIT. |

## Scaricati dall'app dai siti ufficiali (non inclusi nel pacchetto)

| Componente | Versione | Fonte e licenza |
|---|---|---|
| MetalSharp Wine | 6.0 arm64 | [MetalSharp-Wine](https://github.com/metalsharp/MetalSharp-Wine); Wine LGPL-2.1-or-later. Impronta verificata in `mac/engine-runtime.json`. |
| madVR | 0.92.17 | madshi, da madshi.net; termini in `third-parties/madVR09217/license.txt`. |
| MPC Video Renderer | 0.9.7.2387 | Aleksoid1978, da GitHub; GPL-3.0. |
| LAV Filters | 0.80 | Hendrik Leppkes, da GitHub; GPL-2.0-or-later. |
| XySubFilter | 3.1.0.752 | Cyberbeing, da GitHub. |
| yt-dlp | ultima versione da GitHub | yt-dlp contributors; Unlicense. |

Le impronte SHA-256 degli archivi scaricati sono in `mac/engine-components.json`; l'app le controlla prima di usare i file.

## Icone

- `Assets/Icons.Uniform`: licenze in `Assets/Icons.Uniform/LICENSE.txt` (Apache-2.0) e `Assets/Icons.Uniform/LICENSE.Tabler.txt` (MIT, Tabler Icons di Paweł Kuna).
- `Assets/Icons.Lucide`: [Lucide](https://github.com/lucide-icons/lucide), licenza ISC con le parti derivate da Feather sotto MIT, testo in `Assets/Icons.Lucide/LICENSE.txt`.

## Caratteri

- Inter e Inter Display 4.1 di Rasmus Andersson (https://rsms.me/inter), SIL Open Font License 1.1, testo in `Assets/Fonts/Inter-OFL.txt`.
- Manrope e Rubik, SIL Open Font License 1.1, testi in `Assets/Fonts/OFL-Manrope.txt` e `Assets/Fonts/OFL-Rubik.txt`.

## Audio spaziale
- **LibTrueHD** (https://github.com/superuser404notfound/LibTrueHD), MIT; contiene il crate `truehd` di truehdd (Apache-2.0) e le dipendenze Rust elencate nel suo NOTICE. Collegata dentro libmpv per il TrueHD Atmos in cuffia; licenze in `Contents/Resources/Licenses/LibTrueHD`.
- **FFmpegBuild**, patch `dca-xll-x-heights` (https://github.com/superuser404notfound/FFmpegBuild), LGPL-2.1-or-later: canali alti del DTS:X nella libavcodec del pacchetto.

## Licenza Cinecore

Il codice originale Cinecore è distribuito secondo [PolyForm Noncommercial License 1.0.0](LICENSE).
