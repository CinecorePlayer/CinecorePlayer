# Cinecore Player Avalonia — versione Windows (19/11/2026)

Progetto: `win-native/CinecoreWin/CinecoreWin.csproj` (net9.0-windows, win-x64). Compila **gli stessi sorgenti** dell'app
Mac (`mac-native/CinecoreMac/**/*.cs`, costanti `CINECORE_MAC` = "app Avalonia" e `CINECORE_WINDOWS` = sistema).
I file solo-Mac sono esclusi (`MacOnlyFiles` nel csproj) e sostituiti da `win-native/CinecoreWin/Platform/*.Win.cs`.
Nei file condivisi le differenze sono blocchi `#if CINECORE_WINDOWS` (il progetto Mac compila ancora, verificato).

Avvio (sviluppo): `win-native\CinecoreWin\bin\Debug\net9.0-windows10.0.19041.0\win-x64\CinecorePlayer.exe`.
I componenti (mpv, ffmpeg, madVR, MPC-VR, XySubFilter, yt-dlp, rubberband, libaacs) si cercano in `third-parties\`
accanto all'exe, poi risalendo fino a `CinecorePlayer2025_beta2\third-parties`, poi nell'installazione di Cinecore 2026.

## Cosa e' collegato a Windows
| Mac | Windows |
|---|---|
| VideoHost sotto Wine | `VideoHost.exe` nativo (compilato nel build in `VideoHost\`), ganci Wine spenti; madVR / MPC-VR / XySubFilter caricati dai file di `third-parties` (DllGetClassObject, come il player Windows), LAV dal sistema |
| libmpv in finestra AppKit | `libmpv-2.dll` (x86_64-v3 con AVX2) in una finestra Win32 `--wid` tenuta sotto il player; D3D11 / D3D11VA, uscita WASAPI |
| AudioQueue (CoreAudio) del motore musicale | `WinAudioQueue`: stesse chiamate sopra WASAPI (NAudio); esclusivo/bit perfect = WASAPI esclusivo |
| Uscite CoreAudio | endpoint WASAPI; bitstream rilevato con i formati IEC 61937 del driver |
| Portachiavi / vault | DPAPI, **stesso formato del player Windows** (`dpapi:`), credenziali leggibili da entrambi |
| Frequenza schermo | `RationalDisplayModeSwitcher` del player Windows (23,976 esatti) |
| Tasti multimediali | SystemMediaTransportControls |
| Finder, Cestino, Condividi | Esplora file, Cestino, pannello Condividi di Windows |
| ISO / dischi | Mount-DiskImage, lettori ottici, chiavi in `%APPDATA%\aacs` |
| Sigle/titoli di coda | `IntroOutroDetector.exe` e profilo del player Windows (stesso `introOutroIndex.json`) |

Dati: libreria, ripresa, cronologia, playlist ecc. sono **gli stessi file** del player Windows
(`%APPDATA%\CinecorePlayer2025`). Impostazioni dell'app Avalonia: `%APPDATA%\Cinecore\settings.json`.
Registri: `%LOCALAPPDATA%\Cinecore\Logs`.

## Provato (fuori schermo, audio muto, copia dei dati)
Avvio e libreria con i dati veri; Film, Serie, Musica, Foto, Cartelle, Spotlight, Impostazioni; mpv, madVR e MPC-VR su
clip di prova e su un film vero da M: (0 fotogrammi persi, D3D11); motore musicale su WASAPI (avanzamento, pausa,
brano successivo); compilazione del progetto Mac.

## Non ancora provato / limiti
- Mai provato su uno schermo vero (c'era solo il monitor principale): schermo intero, finestra video sotto l'HUD
  durante spostamenti/riduzione a icona, HDR, cambio di frequenza reale.
- Menu dei DVD (libdvdnav non c'e' su Windows): il disco parte dritto sul film. Sessione web di Genius (WebKit sul
  Mac): su Windows non c'e', i testi usano le altre fonti. CD audio non gestiti.
- Bitstream e uscita esclusiva della musica: codice pronto, non provati con il ricevitore.
