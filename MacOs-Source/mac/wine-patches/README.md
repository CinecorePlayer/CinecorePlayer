# Patch Cinecore per wined3d (Wine 11.21)

Applicate a `~/Library/Caches/Cinecore/src/wine-11.21` e compilate con
`arch -x86_64 make dlls/wined3d/x86_64-windows/wined3d.dll dlls/d3d9/x86_64-windows/d3d9.dll`
in `build64`. Si usano sempre insieme `wined3d.dll` e `d3d9.dll` compilati dallo stesso sorgente: Wine Staging
cambia l'interfaccia fra i due.

- `0001` (superata): PBO per le superfici SYSTEMMEM+DYNAMIC; la mappatura passa da LockRect e tiene il mutex di d3d9.
- `0002` (attuale): lettura asincrona tutta sul thread CS (glReadPixels in PBO + fence, mappatura quando la GPU ha
  finito, copia su thread senza GL nella memoria dell'applicazione), export `cinecore_download_wait`.
  Attivata dal VideoHost con `CINECORE_BRIDGE_ASYNC=1`; thread di copia `CINECORE_COPY_THREADS` (predefinito 2).

Backup dello stato precedente: `dist/backup-20261107-wined3d`.
- `0003` (attuale, include 0002): copia zero GL→Metal via IOSurface. `cinecore_mac.m` (parte Unix di wined3d,
  Objective-C): IOSurface lineare RGhA, swizzling di `newSharedTextureWithDescriptor:`/`newSharedTextureHandle`/
  `initWithMachPort:`/`newSharedTextureWithHandle:` (la texture Metal condivisa nasce sulla IOSurface e si riapre
  sul dispositivo di madVR), `CGLTexImageIOSurface2D` + `glBlitFramebuffer` sul thread CS (framebuffer per
  contesto GL). Destinazione D3D9 con firma `CCIO`+ID. Export `cinecore_surface_create`. Serve anche `wined3d.so`.
  Nel VideoHost: `CINECORE_BRIDGE_ASYNC=1 CINECORE_BRIDGE_ZC=1`.
- `0004` (winemac, attuale): le viste Cocoa create per DXMT (`dxmt_export.c`) seguono le misure della finestra.
  Le superfici client create per DXMT non sono nell'elenco che win32u aggiorna, perché `use_window_client_surface`
  non è esportata. `macdrv_WindowPosChanged` chiama quindi `dxmt_window_pos_changed`, che riallinea le superfici
  della stessa finestra principale e libera quelle delle finestre distrutte. Senza la patch MPC Video Renderer è nero:
  crea la swapchain su una finestra figlia ancora grande 0×0 e la vista resta di quella misura. Si compila con
  `arch -x86_64 make dlls/winemac.drv/winemac.so`. Il winemac.so precedente è in
  `dist/backup-20261107-wined3d/winemac.so.pre-resize`.
- `0005` (wined3d, sopra 0003): handshake della copia asincrona. Il produttore chiama
  `cinecore_download_expect` prima di accodare il comando, e il consumatore usa `cinecore_download_status`
  (0 in attesa, 1 completata, -1 fallita). Una copia non ancora eseguita non eredita più il segnale della precedente.
  Gli errori GL non vengono dichiarati completamenti validi; le tabelle di destinazioni/superfici non hanno più
  il vecchio limite fisso 64/16. Compilare **sia** `wined3d.dll` **sia** `wined3d.so`.
  Nel VideoHost le sonde si riusano insieme all'anello; i buffer vengono restituiti al produttore dopo una query
  `D3D11_QUERY_EVENT` che conferma la lettura GPU. Test della race, senza GPU:
  `mac-native/bench/DownloadHandshakeChecks` (eseguibile win-x64 nel runtime Wine aggiornato).
- `0006` (winemac, sopra 0004): solo la finestra `Cinecore Video` partecipa anche allo spazio fullscreen
  dell'app nativa (`CanJoinAllSpaces` + `FullScreenAuxiliary`) e conserva il livello delle finestre normali.
  Evita il video nero entrando in fullscreen da una riproduzione già iniziata e il video sopra altre applicazioni.
  Compilare `dlls/winemac.drv/winemac.so`; le finestre e i dialoghi Wine ordinari non cambiano comportamento.
- `0007` (winemac, sopra 0006): la finestra `Cinecore Video` lascia passare il mouse verso l'HUD nativo.
- `0008` (winemac, sopra 0007): il VideoHost può rimettere il video subito sotto la finestra nativa senza attivare Wine.
  Ricompilare `dlls/winemac.drv/winemac.so`.
- `0009` (winemac, sopra 0008): mantiene il video sotto l'HUD nello Space a schermo intero. Durante il cambio di Space
  rende invisibile soltanto la superficie Wine; la riproduzione prosegue. Ricompilare `dlls/winemac.drv/winemac.so`.
- `0010` adatta il ponte IOSurface al runtime MetalSharp Wine ARM64. `0011` porta nello stesso runtime la gestione dello
  Space fullscreen e collega la vista Metal di Mullion al livello che interroga l'EDR del display. Si applicano con
  `mac/build-fex-iosurface.sh`.
- `0012` (winemac, sopra 0009 per Wine 11.21 e sopra 0011 per MetalSharp): corregge il crash raro del thread principale
  di Wine (SEGV in `__destroy_helper_block`, processo bloccato al 100% di un core nel gestore delle eccezioni).
  `-[NSOpenGLContext dealloc]` chiama `-clearDrawable`; la versione di Wine mandava al thread principale un blocco che
  usava `super`, quindi tratteneva `self`, cioè un contesto OpenGL già in distruzione. Se il thread principale
  rilasciava il blocco dopo la fine del `dealloc`, rilasciava memoria già liberata. Capita quando madVR distrugge e
  ricrea i contesti di wined3d. Trovato l'11/11 con `NSZombieEnabled=YES` (`-[WineOpenGLContext release]: message sent
  to deallocated instance`). Ora i blocchi di `clearDrawable`, `setView:` e `resetSurfaceIfBackingSizeChanged` non
  trattengono il contesto (la chiamata è sincrona) e invocano l'implementazione di `NSOpenGLContext` con
  `objc_msgSendSuper`. Wine 11.21: `make dlls/winemac.drv/winemac.so` in `build64` **senza** `arch -x86_64` (gli
  strumenti di Apple non hanno più la parte x86_64; il Makefile usa già `clang -arch x86_64`). Le patch 0007-0009 non
  erano più applicate nel sorgente in cache e sono state riapplicate; il risultato ha gli stessi simboli del
  `winemac.so` in uso.


## 0013 — presentazione a vsync esatto (12/11/2026)
`dlls/wined3d/cinecore_present.m` (copia canonica: `mac-native/PresentHook/cinecore_present.m`) + righe in `Makefile.in`
(sorgente e framework QuartzCore/CoreVideo/CoreGraphics). Sta in `wined3d.so` perché il caricatore di Wine si riavvia
senza `DYLD_INSERT_LIBRARIES`. Attivo solo con `CINECORE_VSYNC_FILE` (la imposta l'app nativa): sostituisce
`-presentDrawable:afterMinimumDuration:` con un giro del drawable fatto da un timer sul vsync vero e pubblica
l'orologio dei vsync al VideoHost (`VsyncClock.cs`). Serve solo la parte Unix:
`make dlls/wined3d/wined3d.so` in `ms-build`, poi copia in `lib/wine/aarch64-unix/` e firma (vedi
`dist/debug-20261111/prestazioni/mkrt.sh`). Installato in `MetalSharp-Wine-IOSurface.app` (vecchio in
`dist/backups/wined3d.so.IOSurface-prima-present-20261112`).

### 0013, aggiornamento 18/11/2026 — schermo del film
Il display link e la frequenza nominale seguono lo schermo della finestra del film (prima: sempre `CGMainDisplayID()`
e frequenza letta una volta all'avvio, quindi con il proiettore portato a 23,976 Hz la griglia dei vsync restava
quella di 60 Hz divisa per un fattore sbagliato). `track_display` ogni 0,5 s, sul thread principale: vista del
layer del drawable → finestra → `NSScreenNumber`; `CVDisplayLinkSetCurrentCGDisplay` se cambia, frequenza riletta
da `CGDisplayCopyDisplayMode` ogni volta. Opzionale `CINECORE_DISPLAY_ID` come schermo di partenza. Precedente in
`dist/backups/wined3d.so.IOSurface-prima-displaylink-20261118`. Va messo anche nel pacchetto del motore scaricabile.

## 0014 — texture_gl.c cumulativo (riferimento)
Differenza completa di `texture_gl.c` rispetto a MetalSharp Wine: contiene 0003/0005/0010 più il blocco
"Cinecore pipeline" (consegna in catena, `CINECORE_PIPELINE=1`, sperimentale e NON usata: con la coda flip dà lampi).

## 0018 — registro delle eccezioni Objective-C (14/11/2026)
`cocoa_main.m`: `NSSetUncaughtExceptionHandler` scrive su stderr (registro .stderr del VideoHost) nome, motivo e indirizzi
dello stack con modulo e simbolo (`dladdr`). Serve a trovare l'eccezione "insertObject:atIndex: object cannot be nil"
che il 14/11 chiudeva il VideoHost all'avvio di madVR (stack vuoto nel messaggio di AppKit). MetalSharp ARM64:
`make dlls/winemac.drv/winemac.so` in `ms-build`, copia in `lib/wine/aarch64-unix/` e firma. Precedente in
`dist/backups/winemac.so.arm64-prima-eccezioni-20261114`.

## 0019 — preprocessore delle eccezioni (14/11/2026, sopra 0018)
`cocoa_main.m`: `objc_setExceptionPreprocessor` installato al caricamento di `winemac.so` (costruttore). A ogni
`NSInvalidArgumentException` scrive nel registro .stderr del VideoHost nome, motivo e `[NSThread callStackSymbols]`
al momento del LANCIO: il gestore di 0018 viene rimpiazzato da AppKit e lo stack restava vuoto. Il diff contiene anche 0018.
Installato in `MetalSharp-Wine-IOSurface.app`; precedente in `dist/backups/winemac.so.arm64-prima-preprocessore-20261114`.

## 0020 — controllo della fence nella consegna in catena (19/11/2026, INSTALLATA)
`0020-wined3d-cinecore-fence-check.diff` (texture_gl.c, solo wined3d.dll aarch64-windows): con CINECORE_PIPELINE il posto IOSurface del
fotogramma precedente veniva liberato dopo glClientWaitSync(500 ms) senza guardare il risultato. Ora: scaduta = il posto resta in lettura,
fallita = fotogramma segnato fallito. Nelle prove non scatta mai. A/B sul virtuale (3 coppie alternate): 8,0 contro 8,5 persi/s, nel rumore.
Precedente in dist/backups/wined3d.dll.IOSurface-prima-fence-20261119.
ATTENZIONE MISURE: sul virtuale in risparmio energetico un singolo giro varia da 7 a 11 persi/s anche con lo STESSO DLL (prova: DLL identico
con la sola data cambiata = 7,1-9,1). Mai concludere da una coppia: almeno 3 coppie alternate. La ricompilazione in ms-build e' sana
(DLL ricompilato dal sorgente originale = identico al bit salvo le due date).
