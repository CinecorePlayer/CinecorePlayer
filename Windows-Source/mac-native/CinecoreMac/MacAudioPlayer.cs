using System;
using System.Runtime.InteropServices;

namespace CinecoreMac;

/// <summary>
/// Riproduzione della musica con AVPlayer di macOS (al posto di CinecoreAudioEngine/WASAPI di Windows): file locali
/// e stream http dei server (Jellyfin, DLNA). Nessun controllo del volume del ricevitore: decide il volume di sistema.
/// </summary>
internal sealed class MacAudioPlayer : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFloat(IntPtr target, IntPtr selector, float value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern float GetFloat(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CMTime GetTime(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SeekTime(IntPtr target, IntPtr selector, CMTime time, CMTime before, CMTime after);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr StringWithUtf8(IntPtr target, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct CMTime
    {
        public long Value; public int Timescale; public uint Flags; public long Epoch;
        public double Seconds => (Flags & 1) != 0 && Timescale > 0 ? (double)Value / Timescale : double.NaN;
        public static CMTime From(double seconds) => new() { Value = (long)Math.Round(seconds * 1000), Timescale = 1000, Flags = 1 };
    }

    private static readonly IntPtr Release = sel_registerName("release"), Retain = sel_registerName("retain");
    private IntPtr _player;

    static MacAudioPlayer() => dlopen("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", 1);

    public string? Source { get; private set; }

    public void Open(string pathOrUrl)
    {
        Stop();
        bool web = pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        IntPtr text = StringWithUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), pathOrUrl);
        IntPtr url = web
            ? Send(objc_getClass("NSURL"), sel_registerName("URLWithString:"), text)
            : Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), text);
        if (url == IntPtr.Zero) return;
        _player = Send(Send(objc_getClass("AVPlayer"), sel_registerName("playerWithURL:"), url), Retain);
        Source = pathOrUrl;
        SetOutput(MacAudioDevices.ConfiguredOutputUid);
        // Solo per le prove automatiche: nessun suono.
        ApplyVolume();
        Play();
    }

    public void SetOutput(string uid)
    {
        if (_player == IntPtr.Zero) return;
        IntPtr identifier = uid.Length == 0 ? IntPtr.Zero :
            StringWithUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), uid);
        Send(_player, sel_registerName("setAudioOutputDeviceUniqueID:"), identifier);
    }

    private float _volume = 1f;
    private bool _muted;

    /// <summary>Volume della musica (0..1) e muto: restano uguali da un brano all'altro, come la barra di Windows.</summary>
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0f, 1f); ApplyVolume(); } }
    public bool Muted { get => _muted; set { _muted = value; ApplyVolume(); } }

    private void ApplyVolume()
    {
        if (_player == IntPtr.Zero) return;
        // Solo per le prove automatiche: nessun suono.
        bool qaMute = Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1";
        SendFloat(_player, sel_registerName("setVolume:"), qaMute || _muted ? 0f : _volume);
    }

    public void Play() { if (_player != IntPtr.Zero) Send(_player, sel_registerName("play")); }
    public void Pause() { if (_player != IntPtr.Zero) Send(_player, sel_registerName("pause")); }
    public bool IsPlaying => _player != IntPtr.Zero && GetFloat(_player, sel_registerName("rate")) > 0;

    public double Position
    {
        get { double s = _player == IntPtr.Zero ? 0 : GetTime(_player, sel_registerName("currentTime")).Seconds; return double.IsFinite(s) ? Math.Max(0, s) : 0; }
    }

    public double Duration
    {
        get
        {
            if (_player == IntPtr.Zero) return 0;
            IntPtr item = Send(_player, sel_registerName("currentItem"));
            double s = item == IntPtr.Zero ? 0 : GetTime(item, sel_registerName("duration")).Seconds;
            return double.IsFinite(s) ? s : 0;
        }
    }

    /// <summary>0 = in corso o in caricamento, 1 = pronto, 2 = errore (AVPlayerItemStatus).</summary>
    public bool Failed
    {
        get
        {
            if (_player == IntPtr.Zero) return false;
            IntPtr item = Send(_player, sel_registerName("currentItem"));
            return item != IntPtr.Zero && Send(item, sel_registerName("status")) == (IntPtr)2;
        }
    }

    public void Seek(double seconds)
    {
        if (_player == IntPtr.Zero) return;
        var zero = new CMTime { Flags = 1, Timescale = 1 };
        SeekTime(_player, sel_registerName("seekToTime:toleranceBefore:toleranceAfter:"), CMTime.From(Math.Max(0, seconds)), zero, zero);
    }

    public void Stop()
    {
        if (_player == IntPtr.Zero) return;
        Pause();
        Send(_player, Release);
        _player = IntPtr.Zero;
        Source = null;
    }

    public void Dispose() => Stop();
}
