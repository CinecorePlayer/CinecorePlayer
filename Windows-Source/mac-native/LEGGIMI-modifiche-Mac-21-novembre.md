# Modifiche del 21 novembre da portare sul Mac

I file sono già quelli completi: si copiano sopra quelli del progetto, nella stessa cartella (`Utilities/` e `mac-native/CinecoreMac/`).

## 1. Volume dell'ampli via IP (barra a scatti di dB)

File: `mac-native/CinecoreMac/MainWindow.Amplifier.cs`, `Utilities/AmplifierControl.cs`.

Prima, quando trascinavi la barra oltre +3 dB dal volume attuale, il player rimetteva il cursore a +3 dB: "tornava indietro". Ora:

- **Barra a dB interi:** la barra è divisa in scatti da 1 dB (1/80 della barra = 1 dB, come il comando `MV` dei Denon/Marantz). Il punto dove la lasci diventa il volume da raggiungere (`_ampGoal`).
- **Il cursore resta fermo** dove l'hai messo finché il ricevitore non ci arriva.
- **Abbassare:** un solo comando.
- **Alzare:** comandi uno dopo l'altro, ciascuno al massimo +3 dB e sempre con la lettura del volume prima (stessa connessione: lettura, controllo, comando). Pausa di 120 ms fra uno e l'altro.
- **Tetto di −20 dB:** la barra non va oltre. Se il ricevitore è già più alto (dal suo telecomando) non si sale. Messaggio: "Limite di sicurezza del player".
- **Fine della salita:** si ferma quando arriva, quando tocca il tetto, quando fai un gesto nuovo, oppure se il ricevitore non segue per due volte di fila.
- **Tasti, rotella e telecomando:** ±1 dB a pressione, partendo dal volume già chiesto (si sommano anche mentre un comando è in viaggio).

Funzioni nuove o riscritte: `TryAmplifierVolume`, `SendAmplifierAsync(device, toggleMute)`, `AmplifierDbText`. In `AmplifierControl.cs` c'è `ReadThenSetAsync` (lettura e comando in una connessione sola).

## 2. Sincronizzazione del testo

File: `Utilities/LyricsSynchronization.cs`, `Utilities/MusicLyricsService.Background.cs`, `mac-native/CinecoreMac/LibraryView.MusicFullscreen.cs`, `LibraryView.MusicBar.cs`.

Tre correzioni in `LyricsSynchronization.cs`:

- **`DropCollapsedRuns`:** whisper a volte ripete un pezzo già trascritto con tutte le parole allo stesso istante (Filthy Pop: 476 parole a 27,1 s). L'allineamento si agganciava lì e tutte le righe finivano a 27 s, oppure il brano veniva scartato. Ora i blocchi di 4 o più parole con lo stesso istante vengono tolti.
- **Copertura del riconosciuto:** si calcola sull'insieme più piccolo fra testo e voce. I siti scrivono i ritornelli una volta sola e la voce li canta tutti: prima questo abbassava il punteggio di brani giusti.
- **`TightenOpening`:** la prima riga si aggancia alle parole subito vicine invece che a una ripetizione più avanti.
- **Motivi di scarto più precisi nel log:** soglia, righe non valide, oppure prima riga non ancorata.

Interfaccia: il pulsante grande "Sincronizza il testo" non c'è più. La voce è nei tre puntini della barra della musica (`LyricSyncMenuEntry`). Mentre lavora, sopra il testo c'è solo una riga piccola con la percentuale.

## 3. Impostazioni del telecomando

File:

- `Utilities/RemoteServer.PageV4.cs` (nuovo, da aggiungere al `.csproj`: la riga è già nel `CinecoreMac.csproj` incluso);
- `Utilities/RemoteServer.PageV2.cs` (aggiunge `RemoteV4Addon()`);
- `mac-native/CinecoreMac/SettingsView.Remote.cs` (nuovo);
- `SettingsView.cs`, `SettingsView.Devices.cs`, `SettingsView.CinecoreAudio.cs` (righe `_rec?....` che annotano le voci);
- `MainWindow.Remote.cs`.

Come funziona:

- **Le voci vengono dal player:** le schede Generali, Dispositivi e Cinecore Audio, mentre si disegnano in una bitmap minuscola, annotano sezioni, righe e controlli. Il telefono riceve proprio quelle.
- **Un tocco sul telefono** esegue lo stesso clic del player. I menu a scelta si leggono senza aprirli.
- **Voci nuove:** una voce aggiunta nella finestra Impostazioni compare da sola anche sul telefono.
- **Sul telefono:** titolo "Impostazioni" e due menu come nel player, "Player: …" e "Componenti avanzati …". In fondo al menu Player ci sono In riproduzione, Questo PC e Questo telecomando. Le sezioni hanno il titolo maiuscolo con una linea sopra, i gruppi dei componenti sono a pillole e le scelte si aprono in un menu a tendina.

## 4. Anteprima della barra del tempo e ricerca

TimelinePreview.cs e MenuGlass.cs: sul Mac non cambia niente (la parte nuova e' sotto #if CINECORE_WINDOWS; MapToSdr e' solo diventato internal). La lentezza su Windows veniva dalla decodifica HEVC 4K su un core del PC, che e' molto piu' lento di un core Apple Silicon. Su Windows ora decodifica la scheda video tramite un libmpv nascosto (GpuFramePreview.Win.cs). LibraryView.Search.cs e SpotlightView.cs: vetro dal vivo nella pillola di ricerca disegnata (sul Mac resta il vetro nativo).

## 5. Analisi HDR/SDR, vetro, animazioni (21 novembre, pomeriggio)

- **HdrAnalyzer.Mac.cs, HdrAnalysisView.cs, MainWindow.HdrAnalysis.cs:** sul Mac il comportamento non cambia, perche' il decoder della scheda video di Windows e' sotto `#if CINECORE_WINDOWS`. `LiveMeter` ora e' IDisposable e la scheda lo chiude. La copia dei pixel nelle bitmap degli strumenti non crea piu' un array per riga. Il messaggio "video salvato sul Mac/PC" dipende dalla piattaforma.
- **BackdropGlass.cs:** `Liquid(...)` e' il materiale nuovo di Windows, con riflesso, bordo e ombra. Sul Mac si usa ancora il costruttore classico, invariato.
- **LayerFades.cs e MainWindow.cs:** le schede entrano in dissolvenza allargandosi dal 98% ed escono rimpicciolendo. Sincronizzazione audio e Sottotitoli hanno solo la dissolvenza, perche' si scalano da sole. Spotlight, editor foto e PiP sfumano anche in uscita, la libreria solo in entrata. Vale anche sul Mac.
- **MainWindow.Qa.cs:** azione di prova `tone:dark|light`.

## 6. PiP di Windows (21 novembre, sera)
Su Windows il PiP è tornato quello del vecchio player nativo: una finestra separata (`PipWindow` + `PipPanel.cs`,
tutto sotto `#if CINECORE_WINDOWS`) e il player ridotto a icona invece che rimpicciolito. Sul Mac non cambia
niente: `TogglePip`/`RestoreFromPip` in `MainWindow.Pip.cs` seguono ancora la strada di prima (le righe nuove
sono dentro `#if CINECORE_WINDOWS`). `MainWindow.cs` usa `VideoOwner` (il player o la finestra del PiP) negli
stessi punti in cui prima usava `this` per l'ordine delle finestre; sul Mac `VideoOwner` è definito in
`MainWindow.Pip.cs` e vale sempre il player, quindi lì non cambia nulla. `MpvEngine.cs` ha un comando
`margins` nuovo, anche lui solo per Windows.

## 7. Mini player della musica e schermo intero (21 novembre, sera)
- `MainWindow.MiniPlayer.cs`: misura predefinita 420 punti (prima 340) e misura/posizione ricordate
  (impostazione `miniBounds`), anche sul Mac. Su Windows il mini player è senza cornice; il trascinamento e il
  ridimensionamento dai bordi stanno in `LibraryView.MiniChrome.cs`, attivi solo se `LibraryView.MiniChromeless`
  è vero (lo imposta solo Windows): sul Mac il comportamento non cambia.
- `MainWindow.cs`: `BeginFullscreenTransition` ora esce subito su Windows (`#if CINECORE_WINDOWS`); sul Mac
  funziona come prima.

## 8. Animazioni di PiP e mini player, vetro del mini player (21 novembre, sera)
- File nuovo `MainWindow.WindowMorph.cs`: solo Windows (`#if CINECORE_WINDOWS`), sul Mac non compila nulla.
- `MainWindow.Pip.cs`: `RestoreFromPip(bool animate = false)`; sul Mac il parametro non cambia niente.
- `LibraryView.MusicBar.cs`: `MiniRadius` ora vale 16 sul Mac (come prima) e 8 su Windows.
- `MainWindow.MiniPlayer.cs`: all'uscita la misura di prima viene rimessa prima dei minimi della finestra (vale anche
  sul Mac: prima la finestra poteva salvare 1024×660 come sua misura).
- `ContextMenuView.cs`: nuovi `CloseNow()` e `HasGhost`, usati solo dal codice di Windows.
