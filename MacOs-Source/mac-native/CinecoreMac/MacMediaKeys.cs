using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace CinecoreMac;

/// <summary>
/// Tasti multimediali del Mac (F7/F8/F9, cuffie, Centro di Controllo): come i Media keys di Windows. macOS li manda solo
/// all'app "In riproduzione": il player si registra con MPRemoteCommandCenter e aggiorna MPNowPlayingInfoCenter
/// (titolo, artista, durata, posizione, stato), quindi compare anche nel widget In riproduzione.
/// </summary>
internal static unsafe class MacMediaKeys
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBool(IntPtr obj, IntPtr sel, bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendLong(IntPtr obj, IntPtr sel, long a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDouble(IntPtr obj, IntPtr sel, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendGetDouble(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport("libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport("libCinecoreGlass.dylib")] private static extern IntPtr CinecoreNowPlayingArtwork([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libCinecoreGlass.dylib")] private static extern void CinecoreNowPlayingRelease(IntPtr artwork);

    // Copertina mostrata (MPMediaItemArtwork trattenuto): si ricrea solo quando cambia il file.
    private static string? _artPath;
    private static IntPtr _artwork;

    private static IntPtr Artwork(string? path)
    {
        if (path == _artPath) return _artwork;
        try { if (_artwork != IntPtr.Zero) CinecoreNowPlayingRelease(_artwork); } catch { }
        _artwork = IntPtr.Zero; _artPath = path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return IntPtr.Zero;
        try { _artwork = CinecoreNowPlayingArtwork(path); }
        catch (Exception ex) { Console.Error.WriteLine("[tasti] copertina per In riproduzione non disponibile: " + ex.Message); }
        return _artwork;
    }
    [DllImport("libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

    /// <summary>Comandi: "toggle", "play", "pause", "stop", "next", "previous", "seek:&lt;secondi&gt;".</summary>
    public static event Action<string>? Command;

    private static IntPtr _framework, _target, _center;
    private static bool _ready;

    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>Costante NSString del framework (es. MPMediaItemPropertyTitle).</summary>
    private static IntPtr Constant(string name)
    {
        IntPtr p = dlsym(_framework, name);
        return p == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(p);
    }

    private static IntPtr NSString(string s) => SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), s);
    private static IntPtr NSNumber(double v) => SendDouble(objc_getClass("NSNumber"), Sel("numberWithDouble:"), v);

    public static void Start()
    {
        if (_ready || !OperatingSystem.IsMacOS()) return;
        // Nelle prove in secondo piano il player di prova non deve comparire nel Centro di Controllo dell'utente.
        if (Environment.GetEnvironmentVariable("CINECORE_QA_BACKGROUND") == "1" && Environment.GetEnvironmentVariable("CINECORE_QA_MEDIAKEYS") != "1") return;
        try
        {
            _framework = dlopen("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer", 1);
            if (_framework == IntPtr.Zero) return;
            IntPtr cls = objc_getClass("CinecoreRemoteTarget");
            if (cls == IntPtr.Zero)
            {
                cls = objc_allocateClassPair(objc_getClass("NSObject"), "CinecoreRemoteTarget", 0);
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, long> handler = &Handle;
                foreach (var name in new[] { "toggle:", "play:", "pause:", "stop:", "next:", "previous:", "seek:" })
                    class_addMethod(cls, Sel(name), (IntPtr)handler, "q@:@");
                objc_registerClassPair(cls);
            }
            _target = Send(Send(cls, Sel("alloc")), Sel("init"));
            IntPtr commands = Send(objc_getClass("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
            void Bind(string command, string action)
            {
                IntPtr cmd = Send(commands, Sel(command));
                if (cmd == IntPtr.Zero) return;
                SendBool(cmd, Sel("setEnabled:"), true);
                Send(cmd, Sel("addTarget:action:"), _target, Sel(action));
            }
            Bind("togglePlayPauseCommand", "toggle:");
            Bind("playCommand", "play:");
            Bind("pauseCommand", "pause:");
            Bind("stopCommand", "stop:");
            Bind("nextTrackCommand", "next:");
            Bind("previousTrackCommand", "previous:");
            Bind("changePlaybackPositionCommand", "seek:");
            _center = Send(objc_getClass("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
            _ready = _center != IntPtr.Zero;
        }
        catch (Exception ex) { Console.Error.WriteLine("[tasti] tasti multimediali non disponibili: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static long Handle(IntPtr self, IntPtr cmd, IntPtr evt)
    {
        try
        {
            string name = SelectorName(cmd);
            string command = name.TrimEnd(':');
            if (command == "seek")
            {
                double position = evt != IntPtr.Zero ? SendGetDouble(evt, Sel("positionTime")) : 0;
                command = "seek:" + position.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            Dispatcher.UIThread.Post(() => Command?.Invoke(command));
            return 0; // MPRemoteCommandHandlerStatusSuccess
        }
        catch { return 200; } // CommandFailed
    }

    [DllImport(ObjC)] private static extern IntPtr sel_getName(IntPtr sel);
    private static string SelectorName(IntPtr sel) => Marshal.PtrToStringUTF8(sel_getName(sel)) ?? "";

    /// <summary>
    /// Stato mostrato da macOS: null = niente in riproduzione (il player lascia i tasti alle altre app).
    /// </summary>
    public static void Update(string? title, string? artist, double position, double duration, bool playing, string? artPath = null)
    {
        if (!_ready) return;
        try
        {
            if (title == null)
            {
                Send(_center, Sel("setNowPlayingInfo:"), IntPtr.Zero);
                SendLong(_center, Sel("setPlaybackState:"), 3); // stopped
                return;
            }
            IntPtr dict = Send(objc_getClass("NSMutableDictionary"), Sel("dictionary"));
            void Put(string key, IntPtr value) { IntPtr k = Constant(key); if (k != IntPtr.Zero && value != IntPtr.Zero) Send(dict, Sel("setObject:forKey:"), value, k); }
            Put("MPMediaItemPropertyTitle", NSString(title));
            if (!string.IsNullOrEmpty(artist)) Put("MPMediaItemPropertyArtist", NSString(artist));
            if (duration > 0) Put("MPMediaItemPropertyPlaybackDuration", NSNumber(duration));
            Put("MPNowPlayingInfoPropertyElapsedPlaybackTime", NSNumber(Math.Max(0, position)));
            Put("MPNowPlayingInfoPropertyPlaybackRate", NSNumber(playing ? 1 : 0));
            // Copertina dell'album (o locandina del film): senza, macOS mostra l'icona dell'app.
            Put("MPMediaItemPropertyArtwork", Artwork(artPath));
            Send(_center, Sel("setNowPlayingInfo:"), dict);
            SendLong(_center, Sel("setPlaybackState:"), playing ? 1 : 2); // playing / paused
        }
        catch { }
    }

    internal static void TestCommand(string command) => Command?.Invoke(command);
}
