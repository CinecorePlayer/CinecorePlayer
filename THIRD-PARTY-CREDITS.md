# Cinecore Player - crediti e licenze di terze parti

Questa applicazione include o distribuisce i componenti elencati qui sotto. I componenti di terze parti restano soggetti alle rispettive licenze e ai termini forniti dai loro autori; la licenza PolyForm Noncommercial di Cinecore si applica al codice originale Cinecore, non sostituisce le licenze di questi componenti.

## Programmi e runtime inclusi

| Componente | Versione / build inclusa | Credito e licenza |
|---|---|---|
| .NET Windows Desktop Runtime | 9.0.15 x64, incluso nel publish autonomo | Microsoft; licenze e avvisi Microsoft sono nei file del runtime distribuito. [Termini .NET](https://dotnet.microsoft.com/platform/dotnet). |
| FFmpeg | 7.1.1 full build, gyan.dev | FFmpeg developers; build con `--enable-gpl` e `--enable-version3`. GPL; consultare `third-parties/ffmpeg` e [FFmpeg legal](https://ffmpeg.org/legal.html). |
| libmpv | v0.41.0-690-g1ac687d79, DLL x86-64 e x86-64-v3 | mpv project; licenza GPL-2.0-or-later. [Sito e sorgenti mpv](https://mpv.io/). |
| yt-dlp | build `2026.08.19` | yt-dlp contributors; [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE). |
| LAV Filters | 0.80 | Hendrik Leppkes; GPL-2.0-or-later. Installatore originale incluso in `third-parties/installers`. [Progetto LAV Filters](https://github.com/Nevcairiel/LAVFilters). |
| libaacs, libbdplus | 0.12.0 e 0.2.0, build MSYS2 ucrt64 | VideoLAN; LGPL-2.1-or-later, testo in `third-parties/libaacs/licenses`. Solo le librerie: nessuna chiave e nessun file KEYDB è incluso. [libaacs](https://www.videolan.org/developers/libaacs.html). |
| libgcrypt, libgpg-error | 1.12.4 e 1.61, build MSYS2 ucrt64 | g10 Code; LGPL-2.1-or-later, testo in `third-parties/libaacs/licenses`. Dipendenze di libaacs. |
| MakeMKV | non incluso | GuinpinSoft inc; se l'utente lo ha installato, Cinecore ne usa la libreria `libmmbd64.dll` sul posto. |
| madVR | 0.92.17 | madshi; termini nel file `third-parties/madVR09217/license.txt`. Conservare i file originali e la relativa licenza. |
| MPC Video Renderer | 0.9.7.2387 | MPC Video Renderer contributors; GNU GPL v3, testo completo in `third-parties/MpcVideoRenderer-0.9.7.2387/LICENSE.txt`. |
| XySubFilter | 3.1.0.752 x64 | File originali e istruzioni in `third-parties/XySubFilter_3.1.0.752_x64`. Il pacchetto fornito non contiene un testo di licenza separato: consultare il readme e i termini dell'autore prima della ridistribuzione. |
| MPC Audio Decoder | 1.8.5.59 x64 | Installatore originale incluso. Consultare i termini presentati dall'installatore e il progetto MPC-HC. |
| MPC Audio Renderer | 1.8.4.132 x64 | Installatore originale incluso. Consultare i termini presentati dall'installatore e il progetto MPC-HC. |
| Microsoft Edge WebView2 Runtime | Evergreen, scaricato solo se manca | Componente Microsoft richiesto dalle viste WebView2. Il setup scarica il bootstrapper dal collegamento Microsoft solo dopo il consenso dell'utente; [documentazione Microsoft](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution). |
| 7-Zip SFX | Modulo 25.00 usato per creare il setup | Igor Pavlov; distribuzione secondo `7-Zip License.txt`, incluso nell'installer. [Sito ufficiale](https://www.7-zip.org/). |

Le DLL e gli strumenti FFmpeg, libmpv e yt-dlp restano nella cartella dell'applicazione e vengono usati localmente da Cinecore. Gli installatori dei filtri DirectShow vengono avviati soltanto dopo il consenso esplicito mostrato al termine dell'installazione di Cinecore.

## Dipendenze .NET e NuGet

Le dipendenze NuGet e le relative versioni sono elencate in `CinecorePlayer2025.deps.json`. Tra i pacchetti principali: DirectShowLib, FFmpeg.AutoGen, LiveChartsCore, Makaretu.Dns, Microsoft.Web.WebView2, NAudio, SkiaSharp, Svg.Skia e Windows API Code Pack. Licenza e progetto di ciascun pacchetto sono nei metadati NuGet e nei rispettivi collegamenti; le versioni distribuite sono quelle riportate in `CinecorePlayer2025.deps.json`.

## Icone

Le licenze delle icone incluse sono in `Assets/Icons.Uniform/LICENSE.txt` (Apache-2.0) e `Assets/Icons.Uniform/LICENSE.Tabler.txt` (MIT).

## Licenza Cinecore

Il codice originale Cinecore ÃƒÂ¨ distribuito secondo [PolyForm Noncommercial License 1.0.0](LICENSE) e il testo viene incluso nella directory dell'applicazione.

## Elenco versionato delle dipendenze NuGet

Le informazioni di licenza sono lette dai metadati `.nuspec` dei pacchetti ripristinati per questa build. Quando il pacchetto non dichiara un identificatore o un file di licenza nei metadati, la licenza va verificata nella pagina del progetto collegata.

| Pacchetto | Versione | Licenza nei metadati | Progetto |
|---|---:|---|---|
| DirectShowLib | 1.0.0 | consultare progetto | [progetto](https://github.com/larrybeall/DirectShowLib) |
| ExCSS | 4.3.1 | MIT | [progetto](https://github.com/TylerBrinks/ExCSS) |
| FFmpeg.AutoGen | 7.1.1 | LICENSE.txt | [progetto](https://github.com/Ruslan-B/FFmpeg.AutoGen) |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| HarfBuzzSharp | 8.3.1.1 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| LiveChartsCore.SkiaSharpView.WinForms | 2.0.0-rc6.1 | MIT | [progetto](https://www.nuget.org/packages/LiveChartsCore.SkiaSharpView.WinForms/2.0.0-rc6.1) |
| LiveChartsCore.SkiaSharpView | 2.0.0-rc6.1 | MIT | [progetto](https://www.nuget.org/packages/LiveChartsCore.SkiaSharpView/2.0.0-rc6.1) |
| LiveChartsCore | 2.0.0-rc6.1 | MIT | [progetto](https://www.nuget.org/packages/LiveChartsCore/2.0.0-rc6.1) |
| Makaretu.Dns | 2.0.1 | consultare progetto | [progetto](https://github.com/richardschneider/net-dns) |
| Microsoft.NETCore.Platforms | 3.1.0 | MIT | [progetto](https://github.com/dotnet/corefx) |
| Microsoft.Web.WebView2 | 1.0.3719.77 | LICENSE.txt | [progetto](https://aka.ms/webview) |
| Microsoft.Win32.Registry | 4.7.0 | MIT | [progetto](https://github.com/dotnet/corefx) |
| Microsoft.Win32.SystemEvents | 9.0.0 | MIT | [progetto](https://dot.net/) |
| NAudio.Asio | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio.Core | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio.Midi | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio.Wasapi | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio.WinForms | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio.WinMM | 2.2.1 | MIT | [progetto](https://github.com/naudio/NAudio) |
| NAudio | 2.2.1 | license.txt | [progetto](https://github.com/naudio/NAudio) |
| OpenTK.Audio.OpenAL | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Audio.OpenAL/4.8.2) |
| OpenTK.Compute | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Compute/4.8.2) |
| OpenTK.Core | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Core/4.8.2) |
| OpenTK.GLControl | 4.0.1 | MIT | [progetto](https://github.com/opentk/GLControl) |
| OpenTK.Graphics | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Graphics/4.8.2) |
| OpenTK.Input | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Input/4.8.2) |
| OpenTK.Mathematics | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Mathematics/4.8.2) |
| OpenTK.redist.glfw | 3.3.8.39 | COPYING.md | [progetto](https://www.glfw.org/) |
| OpenTK.Windowing.Common | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Windowing.Common/4.8.2) |
| OpenTK.Windowing.Desktop | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Windowing.Desktop/4.8.2) |
| OpenTK.Windowing.GraphicsLibraryFramework | 4.8.2 | consultare progetto | [progetto](https://www.nuget.org/packages/OpenTK.Windowing.GraphicsLibraryFramework/4.8.2) |
| OpenTK | 4.8.2 | MIT | [progetto](https://github.com/opentk/opentk) |
| ShimSkiaSharp | 3.4.1 | MIT | [progetto](https://github.com/wieslawsoltes/Svg.Skia) |
| SimpleBase | 1.3.1 | consultare progetto | [progetto](https://github.com/ssg/SimpleBase) |
| SkiaSharp.HarfBuzz | 3.119.0 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.NativeAssets.Win32 | 3.119.1 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.Views.Desktop.Common | 3.119.0 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp.Views.WindowsForms | 3.119.0 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| SkiaSharp | 3.119.1 | MIT | [progetto](https://go.microsoft.com/fwlink/?linkid=868515) |
| Svg.Custom | 3.4.1 | MS-PL | [progetto](https://github.com/wieslawsoltes/Svg.Skia) |
| Svg.Model | 3.4.1 | MIT | [progetto](https://github.com/wieslawsoltes/Svg.Skia) |
| Svg.Skia | 3.4.1 | MIT | [progetto](https://github.com/wieslawsoltes/Svg.Skia) |
| System.Drawing.Common | 9.0.0 | MIT | [progetto](https://github.com/dotnet/winforms) |
| System.Runtime.CompilerServices.Unsafe | 5.0.0 | MIT | [progetto](https://github.com/dotnet/runtime) |
| System.Security.AccessControl | 4.7.0 | MIT | [progetto](https://github.com/dotnet/corefx) |
| System.Security.Principal.Windows | 4.7.0 | MIT | [progetto](https://github.com/dotnet/corefx) |
| WindowsAPICodePack-Core | 1.1.2 | consultare progetto | [progetto](https://github.com/aybe/Windows-API-Code-Pack-1.1) |
