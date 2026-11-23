using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// DVD con i menu (il navigatore DVD di DirectShow su Windows), con libdvdnav: il disco si "suona" come un lettore da
/// tavolo (avvio, avvertenze, menu, film) e ne esce un unico flusso MPEG-PS che il server locale (MacDiscStream) passa
/// al motore. I tempi del flusso si riscrivono perche' crescano sempre (i menu ricominciano, le celle saltano), cosi'
/// qualsiasi motore lo legge come una diretta. I pulsanti dei menu (sottoimmagini e evidenziazione) li disegna l'app
/// sopra il video (DvdMenuView): uguali con MPV, madVR e MPC-VR. Ogni azione che cambia punto del disco (pulsante,
/// menu, ricerca, capitolo) apre una nuova "generazione" del flusso e il motore la ricarica.
/// </summary>
internal sealed unsafe class MacDvdNav : IDisposable
{
    // ---------------------------------------------------------------- libdvdnav
    private static IntPtr _lib;
    private static delegate* unmanaged[Cdecl]<IntPtr*, byte*, int> _open;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _close;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _setReadahead, _setPgcPositioning;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _menuLang, _audioLang, _spuLang;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, int*, int> _nextBlock;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _stillSkip, _waitSkip, _goUp;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _currentPci;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int> _currentHighlight;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, byte*, int> _highlightArea;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> _up, _down, _left, _right, _activate;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int> _mouseSelect, _mouseActivate;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _menuCall, _titlePlay;
    private static delegate* unmanaged[Cdecl]<IntPtr, sbyte> _isVts, _isVmgm, _isVtsm;
    private static delegate* unmanaged[Cdecl]<IntPtr, long> _currentTime;
    private static delegate* unmanaged[Cdecl]<IntPtr, ulong, int> _timeSearch;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte> _videoAspect;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> _videoResolution;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int> _titleInfo;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int, int> _partPlay;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, ulong**, ulong*, uint> _describeChapters;
    private static delegate* unmanaged[Cdecl]<IntPtr, sbyte> _activeAudio, _activeSpu;
    private static delegate* unmanaged[Cdecl]<IntPtr, int*, int> _titleCount;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _errString;
    private static delegate* unmanaged[Cdecl]<void*, void> _free;
    private static readonly object LoadLock = new();

    private const int Ok = 1;
    private const int EvBlock = 0, EvStill = 2, EvSpuStream = 3, EvAudioStream = 4, EvVtsChange = 5, EvCellChange = 6,
        EvNav = 7, EvStop = 8, EvHighlight = 9, EvClut = 10, EvHop = 12, EvWait = 13;
    public const int MenuEscape = 0, MenuTitle = 2, MenuRoot = 3, MenuSubtitle = 4, MenuAudio = 5, MenuChapters = 7;

    private static string? LibraryPath()
    {
        foreach (string p in new[] {
            Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libdvdnav.4.dylib"),
            "/opt/homebrew/lib/libdvdnav.4.dylib", "/usr/local/lib/libdvdnav.4.dylib" })
            if (File.Exists(p)) return Path.GetFullPath(p);
        return null;
    }

    public static bool Available => LibraryPath() != null;

    /// <summary>Menu dei DVD (Impostazioni › Riproduzione): spenti, il disco parte dritto sul film come prima.</summary>
    public static bool Enabled { get => MacSettings.Get("dvdMenus", true); set => MacSettings.Set("dvdMenus", value); }

    private static bool Load()
    {
        lock (LoadLock)
        {
            if (_lib != IntPtr.Zero) return true;
            if (LibraryPath() is not { } path) return false;
            string css = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libdvdcss.2.dylib"));
            if (File.Exists(css)) try { NativeLibrary.Load(css); } catch { }
            string read = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "libdvdread.8.dylib"));
            if (File.Exists(read)) try { NativeLibrary.Load(read); } catch { }
            IntPtr lib = NativeLibrary.Load(path);
            IntPtr F(string name) => NativeLibrary.GetExport(lib, name);
            _open = (delegate* unmanaged[Cdecl]<IntPtr*, byte*, int>)F("dvdnav_open");
            _close = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("dvdnav_close");
            _setReadahead = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("dvdnav_set_readahead_flag");
            _setPgcPositioning = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("dvdnav_set_PGC_positioning_flag");
            _menuLang = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)F("dvdnav_menu_language_select");
            _audioLang = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)F("dvdnav_audio_language_select");
            _spuLang = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)F("dvdnav_spu_language_select");
            _nextBlock = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, int*, int>)F("dvdnav_get_next_block");
            _stillSkip = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("dvdnav_still_skip");
            _waitSkip = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("dvdnav_wait_skip");
            _currentPci = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("dvdnav_get_current_nav_pci");
            _currentHighlight = (delegate* unmanaged[Cdecl]<IntPtr, int*, int>)F("dvdnav_get_current_highlight");
            _highlightArea = (delegate* unmanaged[Cdecl]<IntPtr, int, int, byte*, int>)F("dvdnav_get_highlight_area");
            _up = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("dvdnav_upper_button_select");
            _down = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("dvdnav_lower_button_select");
            _left = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("dvdnav_left_button_select");
            _right = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("dvdnav_right_button_select");
            _activate = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)F("dvdnav_button_activate");
            _mouseSelect = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int>)F("dvdnav_mouse_select");
            _mouseActivate = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, int>)F("dvdnav_mouse_activate");
            _menuCall = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("dvdnav_menu_call");
            _isVts = (delegate* unmanaged[Cdecl]<IntPtr, sbyte>)F("dvdnav_is_domain_vts");
            _isVmgm = (delegate* unmanaged[Cdecl]<IntPtr, sbyte>)F("dvdnav_is_domain_vmgm");
            _isVtsm = (delegate* unmanaged[Cdecl]<IntPtr, sbyte>)F("dvdnav_is_domain_vtsm");
            _currentTime = (delegate* unmanaged[Cdecl]<IntPtr, long>)F("dvdnav_get_current_time");
            _timeSearch = (delegate* unmanaged[Cdecl]<IntPtr, ulong, int>)F("dvdnav_time_search");
            _videoAspect = (delegate* unmanaged[Cdecl]<IntPtr, byte>)F("dvdnav_get_video_aspect");
            _videoResolution = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)F("dvdnav_get_video_resolution");
            _titleInfo = (delegate* unmanaged[Cdecl]<IntPtr, int*, int*, int>)F("dvdnav_current_title_info");
            _partPlay = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)F("dvdnav_part_play");
            _describeChapters = (delegate* unmanaged[Cdecl]<IntPtr, int, ulong**, ulong*, uint>)F("dvdnav_describe_title_chapters");
            _activeAudio = (delegate* unmanaged[Cdecl]<IntPtr, sbyte>)F("dvdnav_get_active_audio_stream");
            _activeSpu = (delegate* unmanaged[Cdecl]<IntPtr, sbyte>)F("dvdnav_get_active_spu_stream");
            _titleCount = (delegate* unmanaged[Cdecl]<IntPtr, int*, int>)F("dvdnav_get_number_of_titles");
            _goUp = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("dvdnav_go_up");
            _titlePlay = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("dvdnav_title_play");
            _errString = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("dvdnav_err_to_string");
            _free = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "free");
            _lib = lib;
            return true;
        }
    }

    // ---------------------------------------------------------------- sessioni
    private static readonly Dictionary<string, MacDvdNav> Sessions = new(StringComparer.Ordinal);

    /// <summary>La sessione del disco (una per disco: il motore che si riavvia ritrova lo stesso punto e lo stesso menu).</summary>
    public static MacDvdNav? For(string root, bool scrambled)
    {
        lock (Sessions)
        {
            foreach (var s in Sessions.Values) if (s.Root == root && !s._disposed) return s;
            if (!Load()) return null;
            // I dischi in chiaro si leggono dai file del volume montato (la lettura grezza dal lettore e' lentissima);
            // quelli protetti dal dispositivo, dove libdvdcss trova le chiavi.
            // Per i dischi in chiaro si passa una cartella con un collegamento a VIDEO_TS: libdvdread non riconosce il
            // lettore e legge i file del volume, senza chiedere le chiavi CSS di ogni VOB (3-8 s di blocco all'apertura).
            string path = root;
            if (!scrambled && Directory.Exists(Path.Combine(root, "VIDEO_TS")))
            {
                try
                {
                    string link = Path.Combine(Path.GetTempPath(), "cinecore-dvd-" + (uint)root.GetHashCode());
                    Directory.CreateDirectory(link);
                    string target = Path.Combine(link, "VIDEO_TS");
                    if (Directory.Exists(target) || File.Exists(target)) Directory.Delete(target);
                    Directory.CreateSymbolicLink(target, Path.Combine(root, "VIDEO_TS"));
                    path = link;
                }
                catch (Exception ex) { Console.Error.WriteLine("[dvd] collegamento: " + ex.Message); }
            }
            IntPtr nav;
            byte[] p = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            fixed (byte* pp = p) if (_open(&nav, pp) != Ok || nav == IntPtr.Zero) { Console.Error.WriteLine("[dvd] libdvdnav non apre " + path); return null; }
            var session = new MacDvdNav(root, nav);
            Sessions[session._token] = session;
            Console.Error.WriteLine($"[dvd] navigatore aperto ({(scrambled ? "disco protetto" : "file del volume")})");
            return session;
        }
    }

    public static MacDvdNav? ByToken(string token) { lock (Sessions) return Sessions.TryGetValue(token, out var s) ? s : null; }

    /// <summary>Disco espulso o chiuso: le sessioni si chiudono.</summary>
    public static void CloseAll(string? root = null)
    {
        List<MacDvdNav> closing = new();
        lock (Sessions)
            foreach (var key in new List<string>(Sessions.Keys))
                if (root == null || Sessions[key].Root == root) { closing.Add(Sessions[key]); Sessions.Remove(key); }
        foreach (var s in closing) s.Dispose();
    }

    // ---------------------------------------------------------------- stato
    public string Root { get; }
    private readonly string _token = Guid.NewGuid().ToString("N");
    private IntPtr _nav;
    private readonly object _navLock = new();
    private readonly Thread _producer;
    private volatile bool _disposed;
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>Il motore deve caricare subito il nuovo indirizzo (pulsante, menu, ricerca: quello vecchio non serve piu').</summary>
    public event Action<string>? Restart;
    /// <summary>Menu, pulsanti, sottoimmagine o titolo cambiati: l'app ridisegna.</summary>
    public event Action? Changed;

    // Flusso: generazioni in un anello di memoria condiviso.
    private sealed class Generation { public int Id; public long Written, ReadMax; public bool Done, Abandoned; public DateTime AbandonedAt; }
    private const int RingSize = 32 << 20, Ahead = 6 << 20;
    private readonly byte[] _ring = new byte[RingSize];
    private readonly object _dataLock = new();
    private Generation _gen = new() { Id = 1 };

    // Tempi riscritti (90 kHz): scostamento dei VOBU correnti e fine dell'ultimo, per ricucire i salti.
    private const long StartPts = 90000, Wrap = 1L << 33;
    private long _offset, _lastEnd = -1;
    private readonly List<(double Mapped, double Title)> _segments = new();

    // Stato del disco visto dall'app.
    public bool InMenu { get; private set; } = true;
    public int Title { get; private set; }
    public int Chapter { get; private set; }
    public double Duration { get; private set; }
    public bool Wide { get; private set; } = true;
    public int FrameWidth { get; private set; } = 720;
    public int FrameHeight { get; private set; } = 576;
    public int Buttons { get; private set; }
    public int SelectedButton => _highlightButton;
    public int AudioStream { get; private set; } = -1;
    /// <summary>Gruppo di titoli in riproduzione (VTS_xx).</summary>
    public int TitleSet { get; private set; }
    /// <summary>Un titolo lungo (il film, non avvertenze o trailer): con madVR / MPC-VR passa al motore scelto.</summary>
    public bool InFeature => !InMenu && Duration >= 300;
    /// <summary>Tempo nel titolo al punto di lettura del navigatore.</summary>
    /// <summary>Tempo nel titolo dove e' cominciato il tratto attuale del flusso (scelta nel menu, ricerca, ripresa).</summary>
    public double GenerationStartTitle { get; private set; }
    public double CurrentTitleTime { get { lock (_navLock) return _nav == IntPtr.Zero ? 0 : Math.Max(0, _currentTime(_nav) / 90000.0); } }
    private int _spuPhysical = 0;
    private readonly uint[] _clut = new uint[16];
    /// <summary>Cresce a ogni nuova tavolozza del disco (sottotitoli del film per il motore).</summary>
    public int ClutVersion { get; private set; }

    public string Url => $"http://127.0.0.1:{MacDiscStream.Port}/dvdnav/{_token}/{_gen.Id}.mpg";

    private MacDvdNav(string root, IntPtr nav)
    {
        Root = root; _nav = nav;
        _setReadahead(nav, 1);
        _setPgcPositioning(nav, 1);
        foreach (var lang in new[] { "it", "en" })
        {
            byte[] l = System.Text.Encoding.ASCII.GetBytes(lang + "\0");
            fixed (byte* lp = l) { _menuLang(nav, lp); _audioLang(nav, lp); _spuLang(nav, lp); }
            break;
        }
        MacDiscStream.EnsureStarted();
        _producer = new Thread(Produce) { IsBackground = true, Name = "dvd navigatore" };
        _producer.Start();
    }

    // ---------------------------------------------------------------- produzione del flusso
    private void Produce()
    {
        var block = new byte[2048];
        while (!_disposed)
        {
            int ev = 0, len = 0, status;
            Generation gen;
            int stillLength = -1;
            bool changed = false, endGeneration = false;
            lock (_navLock)
            {
                if (_nav == IntPtr.Zero) return;
                gen = _gen;
                fixed (byte* b = block) status = _nextBlock(_nav, b, &ev, &len);
                if (status != Ok)
                {
                    Console.Error.WriteLine("[dvd] lettura non riuscita: fine del disco");
                    lock (_dataLock) { gen.Done = true; Monitor.PulseAll(_dataLock); }
                    _wake.WaitOne(1000);
                    continue;
                }
                switch (ev)
                {
                    case EvBlock:
                        if (!Rewrite(block)) len = 0;
                        break;
                    case EvNav:
                        OnNav();
                        Rewrite(block);
                        changed = true;
                        break;
                    case EvStill:
                        stillLength = block[0] | block[1] << 8 | block[2] << 16 | block[3] << 24;
                        len = 0;
                        break;
                    case EvWait:
                        _waitSkip(_nav); len = 0;
                        break;
                    case EvVtsChange:
                        // Nuovo gruppo di titoli o passaggio menu <-> film: formato diverso (formato, audio): il motore
                        // finisce quello che ha e riparte dal nuovo indirizzo, che ripresenta i flussi da capo.
                        endGeneration = gen.Written > 0;
                        TitleSet = BitConverter.ToInt32(block, 8);
                        ReadDomain(); changed = true; len = 0;
                        break;
                    case EvCellChange:
                        ReadDomain();
                        long pgcLength = BitConverter.ToInt64(block, 24);
                        if (!InMenu && pgcLength > 0) Duration = pgcLength / 90000.0;
                        changed = true; len = 0;
                        break;
                    case EvHighlight:
                        UpdateHighlight(); changed = true; len = 0;
                        break;
                    case EvClut:
                        for (int i = 0; i < 16; i++) _clut[i] = BitConverter.ToUInt32(block, i * 4);
                        ClutVersion++;
                        _overlayDirty = true; changed = true; len = 0;
                        break;
                    case EvSpuStream:
                        int wide = BitConverter.ToInt32(block, 0), letter = BitConverter.ToInt32(block, 4);
                        int phys = wide >= 0 ? wide : letter;
                        if (phys >= 0 && phys != _spuPhysical) { _spuPhysical = phys & 0x1F; }
                        len = 0;
                        break;
                    case EvAudioStream:
                        AudioStream = _activeAudio(_nav);
                        len = 0;
                        break;
                    case EvStop:
                        lock (_dataLock) { gen.Done = true; Monitor.PulseAll(_dataLock); }
                        len = 0;
                        break;
                    default:
                        len = 0;
                        break;
                }
            }
            if (changed) Changed?.Invoke();
            if (endGeneration) { NextGeneration(restart: false); continue; }
            if (len == 2048) Append(gen, block);
            if (stillLength >= 0) Still(gen, stillLength);
            if (gen.Done && ev == EvStop) _wake.WaitOne(1000);
        }
    }

    /// <summary>Fermo immagine (menu): aspetta l'utente o il tempo indicato; intanto il flusso resta vivo con pacchetti vuoti.</summary>
    private void Still(Generation gen, int seconds)
    {
        // Fine sequenza: il decodificatore mostra subito l'ultimo fotogramma invece di aspettare il successivo.
        Append(gen, SequenceEnd());
        var until = seconds == 0xFF ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
        while (!_disposed && DateTime.UtcNow < until && _gen == gen)
        {
            if (_wake.WaitOne(1000)) break;
            Append(gen, Padding());
        }
        if (_gen == gen && !_disposed) lock (_navLock) if (_nav != IntPtr.Zero) _stillSkip(_nav);
    }

    private static byte[] PackHeader(byte[] b)
    {
        b[0] = 0; b[1] = 0; b[2] = 1; b[3] = 0xBA;
        b[4] = 0x44; b[5] = 0; b[6] = 4; b[7] = 0; b[8] = 4; b[9] = 1; // SCR 0 (non usato: i tempi veri sono nei PES)
        b[10] = 0x01; b[11] = 0x89; b[12] = 0xC3; b[13] = 0xF8;
        return b;
    }

    private static byte[] Padding()
    {
        var b = PackHeader(new byte[2048]);
        b[14] = 0; b[15] = 0; b[16] = 1; b[17] = 0xBE;
        int len = 2048 - 20; b[18] = (byte)(len >> 8); b[19] = (byte)len;
        for (int i = 20; i < 2048; i++) b[i] = 0xFF;
        return b;
    }

    private byte[] SequenceEnd()
    {
        // Un PES video con il solo codice di fine sequenza (00 00 01 B7), senza tempi, in un pacchetto riempito.
        var b = PackHeader(new byte[2048]);
        int p = 14;
        b[p] = 0; b[p + 1] = 0; b[p + 2] = 1; b[p + 3] = 0xE0;
        int pesLen = 3 + 4; b[p + 4] = 0; b[p + 5] = (byte)pesLen;
        b[p + 6] = 0x81; b[p + 7] = 0; b[p + 8] = 0;
        b[p + 9] = 0; b[p + 10] = 0; b[p + 11] = 1; b[p + 12] = 0xB7;
        p += 6 + pesLen;
        b[p] = 0; b[p + 1] = 0; b[p + 2] = 1; b[p + 3] = 0xBE;
        int padLen = 2048 - p - 6; b[p + 4] = (byte)(padLen >> 8); b[p + 5] = (byte)padLen;
        for (int i = p + 6; i < 2048; i++) b[i] = 0xFF;
        return b;
    }

    private void ReadDomain()
    {
        bool vts = _isVts(_nav) != 0;
        InMenu = !vts;
        int t = 0, part = 0;
        _titleInfo(_nav, &t, &part);
        Title = vts ? t : 0; Chapter = vts ? part : 0;
        if (!vts) Duration = 0;
        Wide = _videoAspect(_nav) == 3;
        uint w = 0, h = 0;
        if (_videoResolution(_nav, &w, &h) == Ok && w > 0 && h > 0) { FrameWidth = (int)w; FrameHeight = (int)h; }
        AudioStream = _activeAudio(_nav);
    }

    private void OnNav()
    {
        IntPtr pci = _currentPci(_nav);
        if (pci == IntPtr.Zero) return;
        byte* p = (byte*)pci;
        long s = *(uint*)(p + 12), e = *(uint*)(p + 16);
        if (_lastEnd < 0) _offset = StartPts - s;
        else if (Mod(s + _offset) != _lastEnd) _offset = _lastEnd - s;
        _lastEnd = Mod(e + _offset);
        double mapped = (Mod(s + _offset) - StartPts) / 90000.0;
        double title = _currentTime(_nav) / 90000.0;
        lock (_segments)
        {
            if (_segments.Count == 0) GenerationStartTitle = title;
            _segments.Add((mapped, title));
            if (_segments.Count > 4000) _segments.RemoveRange(0, 1000);
        }
        // Il navigatore legge qualche secondo avanti: i pulsanti di ogni VOBU si tengono con il suo tempo, e i comandi
        // usano quelli del punto che il motore sta mostrando (un menu animato all'inizio non ha ancora pulsanti).
        _readButtons = p[113] & 0x3F;
        var copy = new byte[PciSize];
        Marshal.Copy(pci, copy, 0, PciSize);
        lock (_pciLock)
        {
            _pcis.Add((mapped, (_lastEnd - StartPts) / 90000.0, copy));
            if (_pcis.Count > 3000) _pcis.RemoveRange(0, 1000);
        }
    }

    private const int PciSize = 979;
    private int _readButtons;
    private readonly object _pciLock = new();
    private readonly List<(double Start, double End, byte[] Pci)> _pcis = new();

    /// <summary>Posizione del motore nel flusso (la aggiorna VideoHostClient a ogni "pos").</summary>
    public double EnginePosition
    {
        get => _enginePosition;
        set
        {
            _enginePosition = value;
            int buttons = ShownPci() is { } pci ? pci[113] & 0x3F : 0;
            if (buttons != Buttons)
            {
                Buttons = buttons; _overlayDirty = true;
                if (buttons > 0) lock (_navLock) if (_nav != IntPtr.Zero) UpdateHighlight();
                Changed?.Invoke();
            }
        }
    }
    private double _enginePosition;

    private byte[]? ShownPci()
    {
        lock (_pciLock)
        {
            for (int i = _pcis.Count - 1; i >= 0; i--)
                if (_pcis[i].Start <= _enginePosition + 0.05)
                    return _enginePosition <= _pcis[i].End + 1.5 ? _pcis[i].Pci : null;
            return null;
        }
    }

    private static long Mod(long v) => ((v % Wrap) + Wrap) % Wrap;

    /// <summary>Tempi del pacchetto riscritti; sottoimmagini dei menu tenute per l'app; audio non scelto tolto. False = da scartare.</summary>
    private bool Rewrite(byte[] b)
    {
        if (b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0xBA || (b[4] & 0xC0) != 0x40) return true;
        long scr = ((long)((b[4] >> 3) & 7) << 30) | ((long)(b[4] & 3) << 28) | ((long)b[5] << 20) | ((long)((b[6] >> 3) & 0x1F) << 15) | ((long)(b[6] & 3) << 13) | ((long)b[7] << 5) | (long)(b[8] >> 3);
        long nscr = Mod(scr + _offset);
        b[4] = (byte)(0x40 | ((nscr >> 27) & 0x38) | 0x04 | ((nscr >> 28) & 3));
        b[5] = (byte)(nscr >> 20);
        b[6] = (byte)(((nscr >> 12) & 0xF8) | 0x04 | ((nscr >> 13) & 3));
        b[7] = (byte)(nscr >> 5);
        b[8] = (byte)(((nscr << 3) & 0xF8) | 0x04 | (b[8] & 3));
        int pos = 14 + (b[13] & 7);
        while (pos + 6 <= 2048)
        {
            if (b[pos] != 0 || b[pos + 1] != 0 || b[pos + 2] != 1) break;
            int id = b[pos + 3], length = b[pos + 4] << 8 | b[pos + 5], end = pos + 6 + length;
            if (end > 2048) break;
            if (id is 0xBB or 0xBE or 0xBF || (b[pos + 6] & 0xC0) != 0x80) { pos = end; continue; }
            int flags = b[pos + 7] >> 6, headerLength = b[pos + 8];
            long pts = -1;
            if ((flags & 2) != 0) { pts = ReadTs(b, pos + 9); WriteTs(b, pos + 9, Mod(pts + _offset)); }
            if (flags == 3) WriteTs(b, pos + 14, Mod(ReadTs(b, pos + 14) + _offset));
            int payload = pos + 9 + headerLength;
            bool drop = false;
            if (id == 0xBD && payload < end)
            {
                int sub = b[payload];
                if (sub is >= 0x20 and <= 0x3F)
                {
                    // Sottoimmagini: nei menu le disegna l'app (con l'evidenziazione dei pulsanti), nel film il motore.
                    if (InMenu || _readButtons > 0)
                    {
                        if ((sub & 0x1F) == _spuPhysical) CollectSpu(b, payload + 1, end, pts < 0 ? -1 : Mod(pts + _offset));
                        drop = InMenu;
                    }
                }
                else if (AudioStream >= 0 && AudioNumber(sub) is int n && n >= 0 && n != AudioStream) drop = true;
            }
            else if (id is >= 0xC0 and <= 0xDF && AudioStream >= 0 && (id & 7) != AudioStream) drop = true;
            // Flussi tolti: diventano riempitivo (stessa lunghezza), il pacchetto resta valido.
            if (drop) b[pos + 3] = 0xBE;
            pos = end;
        }
        return true;
    }

    private static int? AudioNumber(int sub) => sub switch
    {
        >= 0x80 and <= 0x87 => sub & 7,   // AC-3
        >= 0x88 and <= 0x8F => sub & 7,   // DTS
        >= 0xA0 and <= 0xA7 => sub & 7,   // LPCM
        _ => null,
    };

    private static long ReadTs(byte[] b, int i) =>
        ((long)((b[i] >> 1) & 7) << 30) | ((long)b[i + 1] << 22) | ((long)(b[i + 2] >> 1) << 15) | ((long)b[i + 3] << 7) | (long)(b[i + 4] >> 1);

    private static void WriteTs(byte[] b, int i, long v)
    {
        b[i] = (byte)((b[i] & 0xF0) | ((v >> 29) & 0x0E) | 1);
        b[i + 1] = (byte)(v >> 22);
        b[i + 2] = (byte)(((v >> 14) & 0xFE) | 1);
        b[i + 3] = (byte)(v >> 7);
        b[i + 4] = (byte)(((v << 1) & 0xFE) | 1);
    }

    // ---------------------------------------------------------------- anello del flusso
    private void Append(Generation gen, byte[] block)
    {
        lock (_dataLock)
        {
            while (!gen.Abandoned && !_disposed && gen.Written - gen.ReadMax > Ahead) Monitor.Wait(_dataLock, 250);
            if (gen.Abandoned || _disposed || gen != _gen) return;
            int at = (int)(gen.Written % RingSize);
            Buffer.BlockCopy(block, 0, _ring, at, block.Length); // 2048 divide RingSize: mai a cavallo della fine
            gen.Written += block.Length;
            Monitor.PulseAll(_dataLock);
        }
    }

    /// <summary>Nuova generazione del flusso: quella di prima si chiude (restart: il motore ricarica subito).</summary>
    private void NextGeneration(bool restart)
    {
        string url;
        lock (_dataLock)
        {
            var old = _gen;
            if (restart) { old.Abandoned = true; old.AbandonedAt = DateTime.UtcNow; }
            else old.Done = true;
            _gen = new Generation { Id = old.Id + 1 };
            Monitor.PulseAll(_dataLock);
            url = Url;
        }
        lock (_segments) { _segments.Clear(); GenerationStartTitle = -1; }
        lock (_pciLock) _pcis.Clear();
        _lastEnd = -1;
        lock (_spuLock) { _spus.Clear(); _spuBuffer = null; }
        _overlayDirty = true;
        _wake.Set();
        Console.Error.WriteLine($"[dvd] flusso {(restart ? "ricaricato" : "continua")}: generazione {_gen.Id}{(InMenu ? " (menu)" : $" (titolo {Title})")}");
        if (restart) Restart?.Invoke(url);
    }

    /// <summary>Generazione che il motore dovrebbe leggere ora (dopo la fine di quella che aveva).</summary>
    public int CurrentGeneration { get { lock (_dataLock) return _gen.Id; } }
    public static int GenerationOf(string url) =>
        int.TryParse(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath), out int g) ? g : 0;

    /// <summary>Risposta HTTP per una generazione: dati man mano che il navigatore li produce, senza lunghezza (diretta).</summary>
    public void Serve(HttpListenerContext context, int genId)
    {
        var response = context.Response;
        Generation? gen;
        lock (_dataLock) gen = _gen.Id == genId ? _gen : null;
        if (gen == null) { response.StatusCode = 404; response.Close(); return; }
        response.ContentType = "video/mpeg";
        response.SendChunked = true;
        if (context.Request.HttpMethod == "HEAD") { response.Close(); return; }
        long pos = 0;
        string? range = context.Request.Headers["Range"];
        if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].Split('-')[0], out long from)) pos = from;
        var chunk = new byte[64 * 1024];
        try
        {
            while (!_disposed)
            {
                int n;
                lock (_dataLock)
                {
                    while (pos >= gen.Written && !gen.Done && !gen.Abandoned && !_disposed) Monitor.Wait(_dataLock, 500);
                    if (gen.Abandoned && pos >= gen.Written) break;
                    if (pos >= gen.Written) break; // generazione finita
                    if (pos < gen.Written - RingSize + Ahead) pos = gen.Written - RingSize + Ahead; // troppo indietro: si riparte dal piu' vecchio rimasto
                    int at = (int)(pos % RingSize);
                    n = (int)Math.Min(Math.Min(gen.Written - pos, chunk.Length), RingSize - at);
                    Buffer.BlockCopy(_ring, at, chunk, 0, n);
                    if (pos + n > gen.ReadMax) gen.ReadMax = pos + n;
                    Monitor.PulseAll(_dataLock);
                }
                response.OutputStream.Write(chunk, 0, n);
                pos += n;
            }
        }
        catch { /* il motore ha chiuso la connessione */ }
        finally { try { response.Close(); } catch { } }
    }

    // ---------------------------------------------------------------- tempi per l'app
    /// <summary>Tempo nel titolo (o nel menu) per la posizione del motore nel flusso.</summary>
    public double TitleTime(double enginePosition)
    {
        lock (_segments)
        {
            for (int i = _segments.Count - 1; i >= 0; i--)
                if (_segments[i].Mapped <= enginePosition + 0.001)
                    return Math.Max(0, _segments[i].Title + (enginePosition - _segments[i].Mapped));
        }
        return 0;
    }

    public List<double> Chapters()
    {
        var list = new List<double>();
        lock (_navLock)
        {
            if (_nav == IntPtr.Zero || Title <= 0) return list;
            ulong* times = null; ulong duration = 0;
            uint n = _describeChapters(_nav, Title, &times, &duration);
            if (times != null)
            {
                list.Add(0);
                for (int i = 0; i + 1 < n; i++) list.Add(times[i] / 90000.0);
                _free(times);
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- comandi
    private bool WithPci(Func<IntPtr, int> action)
    {
        lock (_navLock)
        {
            if (_nav == IntPtr.Zero) return false;
            byte[]? shown = ShownPci();
            if (shown == null || (shown[113] & 0x3F) == 0)
            {
                lock (_pciLock) Console.Error.WriteLine($"[dvd] nessun pulsante a {_enginePosition:0.00} s (vobu {_pcis.Count}, primo {(_pcis.Count > 0 ? _pcis[0].Start : -1):0.00}, ultimo {(_pcis.Count > 0 ? _pcis[^1].Start : -1):0.00})");
                return false;
            }
            int r;
            fixed (byte* pp = shown) r = action((IntPtr)pp);
            if (r == Ok) return true;
            Console.Error.WriteLine("[dvd] comando rifiutato: " + Marshal.PtrToStringUTF8(_errString(_nav)));
            return false;
        }
    }

    public void Move(string direction)
    {
        bool ok = WithPci(pci => direction switch
        {
            "up" => _up(_nav, pci), "down" => _down(_nav, pci), "left" => _left(_nav, pci), _ => _right(_nav, pci),
        });
        if (ok) { lock (_navLock) UpdateHighlight(); Changed?.Invoke(); }
    }

    public void Activate()
    {
        if (WithPci(pci => _activate(_nav, pci))) AfterJump();
    }

    /// <summary>Punto nel fotogramma del DVD (720x576 o 720x480).</summary>
    public bool Hover(int x, int y)
    {
        int before = _highlightButton;
        bool ok = WithPci(pci => _mouseSelect(_nav, pci, x, y));
        if (!ok) return false;
        lock (_navLock) UpdateHighlight();
        if (_highlightButton != before) Changed?.Invoke();
        return true;
    }

    public bool Click(int x, int y)
    {
        if (!WithPci(pci => _mouseActivate(_nav, pci, x, y))) return false;
        AfterJump();
        return true;
    }

    public bool Menu(int menu)
    {
        int r;
        lock (_navLock) r = _nav == IntPtr.Zero ? 0 : _menuCall(_nav, menu);
        if (r != Ok) return false;
        AfterJump();
        return true;
    }

    /// <summary>Indietro (tasto Backspace): il menu precedente del disco.</summary>
    public bool GoUp()
    {
        int r;
        lock (_navLock) r = _nav == IntPtr.Zero ? 0 : _goUp(_nav);
        if (r != Ok) return false;
        AfterJump();
        return true;
    }

    public void Seek(double seconds)
    {
        int r;
        lock (_navLock) r = _nav == IntPtr.Zero || InMenu ? 0 : _timeSearch(_nav, (ulong)(Math.Max(0, seconds) * 90000));
        if (r == Ok) AfterJump();
    }

    /// <summary>Riprendi: il titolo salvato, dal punto salvato (come la ripresa di un lettore da tavolo).</summary>
    public bool PlayTitleAt(int title, double seconds)
    {
        int r;
        lock (_navLock)
        {
            if (_nav == IntPtr.Zero) return false;
            r = _titlePlay(_nav, title);
            if (r == Ok && seconds > 0)
            {
                // La ricerca per tempo vale dentro il titolo: si legge il primo blocco perche' il navigatore entri nel titolo.
                ReadDomain();
                r = _timeSearch(_nav, (ulong)(seconds * 90000)) == Ok ? Ok : r;
            }
        }
        if (r != Ok) return false;
        AfterJump();
        return true;
    }

    public void PlayChapter(int chapter)
    {
        int r;
        lock (_navLock) r = _nav == IntPtr.Zero || Title <= 0 ? 0 : _partPlay(_nav, Title, Math.Max(1, chapter));
        if (r == Ok) AfterJump();
    }

    private void AfterJump()
    {
        lock (_navLock) ReadDomain();
        NextGeneration(restart: true);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- pulsanti e sottoimmagini
    private int _highlightButton;
    private (int Sx, int Sy, int Ex, int Ey, uint Palette) _highlight;
    private volatile bool _overlayDirty = true;

    private void UpdateHighlight()
    {
        int button = 0;
        _currentHighlight(_nav, &button);
        byte[]? shown = ShownPci();
        var area = new byte[20];
        (int, int, int, int, uint) hl = default;
        if (button > 0 && shown != null && (shown[113] & 0x3F) > 0)
            fixed (byte* a = area)
            fixed (byte* pci = shown)
                if (_highlightArea((IntPtr)pci, button, 0, a) == Ok)
                    hl = (BitConverter.ToUInt16(area, 4), BitConverter.ToUInt16(area, 6), BitConverter.ToUInt16(area, 8), BitConverter.ToUInt16(area, 10), BitConverter.ToUInt32(area, 0));
        if (button != _highlightButton || hl != _highlight)
        {
            _highlightButton = button; _highlight = hl; _overlayDirty = true;
        }
    }

    private sealed class Spu { public double Start, End = double.MaxValue; public int X, Y, W, H; public byte[] Index = Array.Empty<byte>(); public byte[] Colors = new byte[4], Alpha = new byte[4]; }
    private readonly object _spuLock = new();
    private readonly List<Spu> _spus = new();
    private byte[]? _spuBuffer; private int _spuFill, _spuSize; private long _spuPts;

    private void CollectSpu(byte[] b, int from, int end, long pts)
    {
        lock (_spuLock)
        {
            if (_spuBuffer == null)
            {
                if (end - from < 2) return;
                _spuSize = b[from] << 8 | b[from + 1];
                if (_spuSize < 4) return;
                _spuBuffer = new byte[_spuSize]; _spuFill = 0; _spuPts = pts;
            }
            int n = Math.Min(end - from, _spuSize - _spuFill);
            Buffer.BlockCopy(b, from, _spuBuffer, _spuFill, n);
            _spuFill += n;
            if (_spuFill < _spuSize) return;
            var data = _spuBuffer; _spuBuffer = null;
            double start = _spuPts < 0 ? (_spus.Count > 0 ? _spus[^1].Start : 0) : (_spuPts - StartPts) / 90000.0;
            if (ParseSpu(data, start) is { } spu)
            {
                _spus.Add(spu);
                if (_spus.Count > 8) _spus.RemoveAt(0);
                _overlayDirty = true;
            }
        }
    }

    private static Spu? ParseSpu(byte[] d, double start)
    {
        try
        {
            int control = d[2] << 8 | d[3];
            var spu = new Spu { Start = start };
            int top = -1, bottom = -1, seq = control;
            bool shown = false;
            for (int guard = 0; guard < 16 && seq + 4 <= d.Length; guard++)
            {
                int date = d[seq] << 8 | d[seq + 1], next = d[seq + 2] << 8 | d[seq + 3];
                int i = seq + 4;
                while (i < d.Length && d[i] != 0xFF)
                {
                    switch (d[i])
                    {
                        case 0x00: case 0x01: shown = true; i++; break;
                        case 0x02: if (shown) spu.End = start + date * 1024 / 90000.0; i++; break;
                        case 0x03: spu.Colors = new[] { (byte)(d[i + 2] & 0xF), (byte)(d[i + 2] >> 4), (byte)(d[i + 1] & 0xF), (byte)(d[i + 1] >> 4) }; i += 3; break;
                        case 0x04: spu.Alpha = new[] { (byte)(d[i + 2] & 0xF), (byte)(d[i + 2] >> 4), (byte)(d[i + 1] & 0xF), (byte)(d[i + 1] >> 4) }; i += 3; break;
                        case 0x05:
                            int x1 = d[i + 1] << 4 | d[i + 2] >> 4, x2 = (d[i + 2] & 0xF) << 8 | d[i + 3];
                            int y1 = d[i + 4] << 4 | d[i + 5] >> 4, y2 = (d[i + 5] & 0xF) << 8 | d[i + 6];
                            spu.X = x1; spu.Y = y1; spu.W = x2 - x1 + 1; spu.H = y2 - y1 + 1; i += 7; break;
                        case 0x06: top = d[i + 1] << 8 | d[i + 2]; bottom = d[i + 3] << 8 | d[i + 4]; i += 5; break;
                        default: i = d.Length; break;
                    }
                }
                if (next == seq) break;
                seq = next;
            }
            if (spu.W <= 0 || spu.H <= 0 || top < 0 || bottom < 0 || spu.W > 2048 || spu.H > 2048) return null;
            spu.Index = new byte[spu.W * spu.H];
            DecodeField(d, top, spu, 0);
            DecodeField(d, bottom, spu, 1);
            return spu;
        }
        catch { return null; }
    }

    private static void DecodeField(byte[] d, int offset, Spu spu, int firstLine)
    {
        int nib = offset * 2;
        int Nibble() { int byteIndex = nib >> 1; if (byteIndex >= d.Length) return 0; int v = (nib & 1) == 0 ? d[byteIndex] >> 4 : d[byteIndex] & 0xF; nib++; return v; }
        for (int y = firstLine; y < spu.H; y += 2)
        {
            int x = 0;
            while (x < spu.W)
            {
                int v = Nibble();
                if (v < 0x4) { v = v << 4 | Nibble(); if (v < 0x10) { v = v << 4 | Nibble(); if (v < 0x40) v = v << 4 | Nibble(); } }
                int run = v >> 2, color = v & 3;
                if (run == 0) run = spu.W - x;
                run = Math.Min(run, spu.W - x);
                if (color != 0) Array.Fill(spu.Index, (byte)color, y * spu.W + x, run);
                x += run;
            }
            if ((nib & 1) != 0) nib++;
        }
    }

    /// <summary>Immagine dei pulsanti da disegnare sopra il video (BGRA premoltiplicato), per la posizione del motore.</summary>
    public sealed record Overlay(byte[] Bgra, int X, int Y, int W, int H, int FrameW, int FrameH, bool Wide);
    private Overlay? _lastOverlay; private Spu? _lastSpu;

    public Overlay? OverlayAt(double enginePosition)
    {
        Spu? spu = null;
        lock (_spuLock)
            for (int i = _spus.Count - 1; i >= 0; i--)
                if (_spus[i].Start <= enginePosition + 0.05) { spu = _spus[i]; break; }
        if (spu != null && enginePosition > spu.End) spu = null;
        if (!InMenu && Buttons == 0) spu = null;
        if (spu == null) return null;
        if (!_overlayDirty && spu == _lastSpu && _lastOverlay != null) return _lastOverlay;
        _overlayDirty = false; _lastSpu = spu;
        var hl = _highlight;
        bool hasHl = _highlightButton > 0 && hl.Ex > hl.Sx;
        var bgra = new byte[spu.W * spu.H * 4];
        uint[] colors = new uint[8];
        for (int i = 0; i < 4; i++)
        {
            colors[i] = Pixel(_clut[spu.Colors[i] & 0xF], spu.Alpha[i]);
            colors[4 + i] = Pixel(_clut[(hl.Palette >> (16 + 4 * i)) & 0xF], (int)((hl.Palette >> (4 * i)) & 0xF));
        }
        fixed (byte* o = bgra)
        {
            uint* px = (uint*)o;
            for (int y = 0; y < spu.H; y++)
            {
                int fy = spu.Y + y;
                bool rowHl = hasHl && fy >= hl.Sy && fy <= hl.Ey;
                for (int x = 0; x < spu.W; x++)
                {
                    int c = spu.Index[y * spu.W + x];
                    int fx = spu.X + x;
                    px[y * spu.W + x] = rowHl && fx >= hl.Sx && fx <= hl.Ex ? colors[4 + c] : colors[c];
                }
            }
        }
        _lastOverlay = new Overlay(bgra, spu.X, spu.Y, spu.W, spu.H, FrameWidth, FrameHeight, Wide);
        return _lastOverlay;
    }

    private static uint Pixel(uint ycrcb, int alpha4)
    {
        if (alpha4 <= 0) return 0;
        double yv = (ycrcb >> 16) & 0xFF, cr = (ycrcb >> 8) & 0xFF, cb = ycrcb & 0xFF;
        double yy = 1.164 * (yv - 16);
        int r = (int)Math.Clamp(yy + 1.596 * (cr - 128), 0, 255);
        int g = (int)Math.Clamp(yy - 0.813 * (cr - 128) - 0.391 * (cb - 128), 0, 255);
        int b = (int)Math.Clamp(yy + 2.018 * (cb - 128), 0, 255);
        int a = alpha4 * 17;
        // Premoltiplicato (come lo vuole Avalonia per Bgra8888 / Premul).
        return (uint)(a << 24 | (r * a / 255) << 16 | (g * a / 255) << 8 | (b * a / 255));
    }

    /// <summary>Tavolozza RGB dei sottotitoli per il motore (opzione "palette" del decodificatore dvdsub di FFmpeg).</summary>
    public string SubtitlePalette()
    {
        var parts = new string[16];
        for (int i = 0; i < 16; i++)
        {
            uint px = Pixel(_clut[i], 15);
            parts[i] = $"{(px >> 16) & 0xFF:x2}{(px >> 8) & 0xFF:x2}{px & 0xFF:x2}";
        }
        return string.Join(",", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        lock (_dataLock) Monitor.PulseAll(_dataLock);
        _producer.Join(2000);
        lock (_navLock) { if (_nav != IntPtr.Zero) { _close(_nav); _nav = IntPtr.Zero; } }
    }
}
