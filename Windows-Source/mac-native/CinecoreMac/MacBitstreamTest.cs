using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// Prova del ricevitore (Impostazioni › Riproduzione, uscita HDMI che dichiara solo PCM): suona per alcuni secondi un
/// flusso compresso di SILENZIO nel formato scelto, dentro il PCM, sull'uscita del film. Se il ricevitore lo riconosce
/// mostra il formato (Dolby Digital, DTS, TrueHD…) e non si sente nulla; se non lo riconosce lo suona come PCM e si
/// sente un fruscio: per questo si prova a volume basso e la prova dura poco. Il flusso si crea con ffmpeg alla prima
/// prova (cache in ~/Library/Caches/Cinecore/bitstream-test). DTS-HD MA non ha un codificatore libero: si prova con un film.
/// </summary>
internal static class MacBitstreamTest
{
    private const string Lib = "libmpv";
    [DllImport(Lib)] private static extern IntPtr mpv_create();
    [DllImport(Lib)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] private static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib)] private static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern void mpv_free(IntPtr data);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    private static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public const int Seconds = 6;
    private static int _running;
    public static bool Running => Volatile.Read(ref _running) != 0;

    /// <summary>Si puo' provare questo formato (serve un codificatore di ffmpeg)?</summary>
    public static bool Testable(string codec) => codec is "ac3" or "dts" or "eac3" or "truehd";

    private static string? EncoderArgs(string codec) => codec switch
    {
        "ac3" => "-c:a ac3 -b:a 448k",
        "eac3" => "-c:a eac3 -b:a 640k",
        "dts" => "-c:a dca -strict -2 -b:a 1509k",
        "truehd" => "-c:a truehd -strict -2",
        _ => null,
    };

    private static async Task<string?> TestFileAsync(string codec)
    {
        if (EncoderArgs(codec) is not { } args || MacTools.Ffmpeg is not { } ffmpeg) return null;
        string dir = System.IO.Path.Combine(global::CinecoreMac.PlatformPaths.CinecoreCaches, "bitstream-test");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, codec + ".mka");
        if (File.Exists(file) && new FileInfo(file).Length > 1000) return file;
        string temp = file + ".tmp.mka";
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in $"-v error -y -f lavfi -i anullsrc=r=48000:cl=5.1 -t {Seconds + 2} {args}".Split(' ')) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(temp);
        using var p = Process.Start(psi);
        if (p == null) return null;
        _ = p.StandardOutput.ReadToEndAsync();
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(temp)) { Console.Error.WriteLine("[audio] prova bitstream: ffmpeg " + err.Trim()); return null; }
        File.Move(temp, file, true);
        return file;
    }

    /// <summary>
    /// Suona la prova. Ritorna il formato aperto da mpv ("spdif-ac3"…), oppure null se l'uscita non ha accettato il
    /// trasporto (mpv torna allora al PCM e si ferma subito: niente da ascoltare).
    /// </summary>
    public static async Task<string?> RunAsync(string? uid, string codec)
    {
        if (!MpvEngine.Available || Interlocked.Exchange(ref _running, 1) != 0) return null;
        try
        {
            string? file = await TestFileAsync(codec);
            if (file == null) return null;
            return await Task.Run(() => Play(uid, codec, file));
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static string? Play(string? uid, string codec, string file)
    {
        MpvEngine.EnsureResolver();
        IntPtr ctx = mpv_create();
        if (ctx == IntPtr.Zero) return null;
        try
        {
            void Opt(string k, string v) => mpv_set_option_string(ctx, U(k), U(v));
            Opt("vid", "no"); Opt("sid", "no"); Opt("audio-display", "no"); Opt("terminal", "no");
            Opt("idle", "yes"); Opt("keep-open", "no");
            string resolved = MacAudioDevices.ResolveUid(uid);
            if (resolved.Length > 0) Opt("audio-device", MpvAudioNames.DevicePrefix + resolved);
#if CINECORE_WINDOWS
            MpvAudioNames.Exclusive(Opt);
#else
            Opt("ao", "coreaudio_exclusive");
            Opt("coreaudio-spdif-hack", "yes");
#endif
            // Solo prove automatiche: i pacchetti su file invece che sull'uscita (niente audio vero di notte).
            string expectedAo = MpvAudioNames.ExclusiveAo;
            if (Environment.GetEnvironmentVariable("CINECORE_QA_BITSTREAM_FILE") is { Length: > 0 } qaFile)
            {
                Opt("ao", "pcm"); Opt("ao-pcm-file", qaFile); Opt("ao-pcm-waveheader", "no");
                expectedAo = "pcm";
            }
            Opt("audio-spdif", codec);
            // Se il trasporto non si apre mpv decodificherebbe in PCM: qui no, la prova deve restare muta.
            Opt("audio-fallback-to-null", "yes");
            // In pausa: l'uscita si apre e si controlla cosa ha negoziato PRIMA che parta un solo pacchetto.
            Opt("pause", "yes");
            if (mpv_initialize(ctx) < 0) return null;
            MacAppIcon.RestoreSoon(); // libmpv mette la sua icona all'app
            Command(ctx, "loadfile", file);
            string? format = null;
            var clock = Stopwatch.StartNew();
            while (format == null && clock.ElapsedMilliseconds < 4000)
            {
                Thread.Sleep(50);
                format = Prop(ctx, "audio-out-params/format");
            }
            string? ao = Prop(ctx, "current-ao");
            Console.Error.WriteLine($"[audio] prova bitstream {codec}: uscita {format ?? "-"} ({ao ?? "-"})");
            // PCM decodificato = il trasporto non e' stato accettato: ci si ferma senza suonare.
            if (format == null || !format.StartsWith("spdif-", StringComparison.Ordinal) || ao != expectedAo) return null;
            mpv_set_property_string(ctx, U("pause"), U("no"));
            clock.Restart();
            while (clock.ElapsedMilliseconds < Seconds * 1000)
            {
                Thread.Sleep(100);
                if (Prop(ctx, "current-ao") != expectedAo && expectedAo != "pcm") return null;
            }
            return format;
        }
        finally { mpv_terminate_destroy(ctx); }
    }

    private static void Command(IntPtr ctx, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            mpv_command(ctx, ptrs);
        }
        finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    private static string? Prop(IntPtr ctx, string name)
    {
        IntPtr p = mpv_get_property_string(ctx, U(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { mpv_free(p); }
    }
}
