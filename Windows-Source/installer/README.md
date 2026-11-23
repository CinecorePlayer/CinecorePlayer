# Installer Windows — CinecorePlayer2026 Beta 4.1

`Build-Installer-Avalonia.ps1` pubblica Cinecore per Windows x64 con il runtime .NET incluso e crea, con Inno Setup, il programma di installazione `CinecorePlayer2026-Beta4.1-Windows-Setup.exe` in `artifacts\installer` (con il file `.sha256.txt`).

L'installer:

1. si apre in italiano o in inglese secondo la lingua di Windows, con lo stile Windows 11 chiaro o scuro come il sistema e le immagini Cinecore;
2. mostra e richiede l'accettazione della licenza PolyForm Noncommercial di Cinecore;
3. installa in `C:\Program Files\Cinecore Player 2026` (modificabile; un aggiornamento resta nella cartella della versione precedente), crea il collegamento nel menu Start e, se scelto, sul desktop;
4. propone i componenti di terze parti, selezionati per impostazione predefinita e deselezionabili: LAV Filters e i decoder/renderer audio MPC con i loro installatori originali visibili; madVR, MPC Video Renderer e XySubFilter registrati direttamente dalla cartella del programma; WebView2 Runtime scaricato da Microsoft solo se manca;
5. registra Cinecore in "App installate", con disinstallazione che deregistra anche i filtri; i dati dell'utente in `%APPDATA%\CinecorePlayer2025` restano;
6. aggiorna sopra un'installazione precedente, chiudendo Cinecore se è aperto.

FFmpeg, libmpv e yt-dlp sono inclusi nella cartella del programma.

## Creazione

Servono .NET SDK 9 e Inno Setup 6.6 o successivo (`scoop bucket add extras; scoop install inno-setup`). Da PowerShell nella directory del repository:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer-Avalonia.ps1
```

Versione e nome del file vengono da `win-native\CinecoreWin\CinecoreWin.csproj`: `<Version>` (ora `0.4.1`) e `<InformationalVersion>` (ora `Beta 4.1`, da cui `CinecorePlayer2026-Beta4.1-Windows-Setup.exe`). L'eseguibile si chiama `CinecorePlayer2026.exe` (`<AssemblyName>`); l'installer cancella il vecchio `CinecorePlayer2025.exe` e i collegamenti "Cinecore Player" delle versioni precedenti.

## Pubblicare un aggiornamento

Il player installato controlla le release di `CinecorePlayer/CinecorePlayer` su GitHub e si aggiorna da solo. Perché una release venga proposta:

1. alza `<Version>` nel progetto e crea l'installer;
2. pubblica la release con il tag `beta-v` + la versione + `-Avalonia-Windows`, per la Beta 4.1 `beta-v0.4.1-Avalonia-Windows` (il player su Windows guarda solo i tag con `Avalonia-Windows`; quelli del Mac e le vecchie `beta-v0.3.x` vengono ignorati);
3. allega l'installer così com'è (un `.exe`, nome senza spazi) e il suo `.sha256.txt`.

Il player scarica l'installer, ne confronta l'impronta SHA-256 con quella che GitHub pubblica per il file, e lo avvia in modalità aggiornamento (`/SILENT /RELAUNCH=1`, senza gli installatori di terze parti): Windows chiede il consenso e a fine installazione il player riparte. Il testo della release compare come elenco delle novità.

Il builder esclude il log di debug, i file `.pdb` e `third-parties\installers\setting.reg`, che contiene un percorso utente locale obsoleto. Le grafiche dell'installer sono in `installer\assets`; l'icona è `Assets\cinecore.ico`.
