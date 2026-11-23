# Telecomando e playlist: codice da portare sul Mac

Copia del 20 novembre 2026, presa dalla cartella `Cinecore_Windows` (versione Windows aggiornata).
I percorsi qui dentro sono gli stessi del progetto: basta sovrascrivere i file con lo stesso nome.

## Telecomando (pagina web sul telefono)
- `Utilities/RemoteServer.cs`: server HTTP, PIN, comandi.
- `Utilities/RemoteServer.PageV2.cs`: la pagina di base (HTML, CSS e JS).
- `Utilities/RemoteServer.PageV3.cs`: lo stile attuale, sopra la V2. Contiene anche le impostazioni rifatte.
  Attenzione: non mettere `display` sulle righe `.setting-row`, perché rompe il filtro delle categorie.
- `Utilities/RemoteNetworkAccess.cs`, `Utilities/QrCode.cs`: accesso dalla rete e codice QR.
- `Utilities/SpanishTexts.Remote.cs`: testi in spagnolo del telecomando.
- `mac-native/CinecoreMac/MainWindow.Remote.cs` e `MainWindow.RemoteFilm.cs`: il collegamento con il player.
- `mac-native/CinecoreMac/RemotePointer.cs`: il puntatore a schermo.
- `mac-native/CinecoreMac/LibraryView.Music.cs`: lo stato della musica mandato al telefono (`RemoteMusicState`),
  con volume e muto, che passano dall'ampli quando la musica suona lì.

## Playlist
- `mac-native/CinecoreMac/LibraryView.Playlists.cs`: pagina e scheda delle playlist (disegno e logica).
- `mac-native/CinecoreMac/LibraryView.Queue.cs`: la coda (stesso stile).
- `mac-native/CinecoreMac/WplPlaylist.cs`: lettura e scrittura dei file .wpl.
- `Utilities/PlaylistStore.cs`: salvataggio delle playlist.

## Da tenere a mente
- I blocchi `#if CINECORE_WINDOWS` sono solo per Windows: sul Mac vengono saltati da soli.
- `LibraryView.Music.cs` usa dei ganci (`AmplifierSetVolume`, `AmplifierStepVolume`, `AmplifierToggleMute`, `AmplifierState`)
  che su Windows collega `MainWindow.Amplifier.cs`. Se sul Mac restano scollegati, volume e muto agiscono sul player.
