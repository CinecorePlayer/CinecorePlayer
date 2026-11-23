# Componenti di terze parti

Questa cartella è vuota nel repository: i componenti sono binari di altri progetti, con licenze proprie, e alcuni
superano i limiti di GitHub. La build li copia accanto all'app (`CopyThirdPartiesToPublish` in
`win-native/CinecoreWin/CinecoreWin.csproj`). Il modo più semplice per averli è copiare la cartella `third-parties`
dall'installazione o dalla release pronta.

Struttura attesa:

```
third-parties/
├─ ffmpeg/win-x64/        ffmpeg.exe, ffprobe.exe, avcodec-61.dll, avformat-61.dll, avutil-59.dll, ...   (FFmpeg 7.1.1, build gyan.dev "full")
├─ mpv/x86_64/            libmpv-2.dll                                                                    (libmpv v0.41)
├─ mpv/x86_64-v3/         libmpv-2.dll (variante per CPU recenti)
├─ yt-dlp/                yt-dlp.exe, yt-dlp.cmd
├─ madVR09217/            madVR64.ax, madHcNet64.dll, ... (cartella originale di madVR 0.92.17)
├─ MpcVideoRenderer-0.9.7.2387/   MpcVideoRenderer64.ax, LICENSE.txt, ...
├─ XySubFilter_3.1.0.752_x64/     XySubFilter.dll, ...
├─ libaacs/               libaacs.dll, libbdplus.dll, libgcrypt-20.dll, libgpg-error-0.dll, licenses/
├─ rubberband/            rubberband.dll, COPYING.txt
└─ installers/            LAVFilters-0.80.exe, MPC-Audio-Decoder-x64.exe, MPC-Audio-Renderer-x64.exe  (solo per l'installer)
```

Fonti ufficiali: [FFmpeg](https://ffmpeg.org/download.html), [mpv](https://mpv.io/installation/),
[yt-dlp](https://github.com/yt-dlp/yt-dlp/releases), [madVR](https://www.videohelp.com/software/madVR),
[MPC Video Renderer](https://github.com/Aleksoid1978/VideoRenderer/releases),
[LAV Filters](https://github.com/Nevcairiel/LAVFilters/releases), [libaacs](https://www.videolan.org/developers/libaacs.html),
[Rubber Band](https://breakfastquay.com/rubberband/). Versioni e licenze sono in `THIRD-PARTY-CREDITS.md`.

Senza questi file il player compila e si apre, ma video, YouTube e alcune analisi audio non funzionano.
