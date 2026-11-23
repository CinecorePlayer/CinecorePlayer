using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CinecoreMac;

/// <summary>
/// Le chiamate di AudioQueue (AudioToolbox del Mac) usate dal motore musicale e dall'anteprima del mix, rifatte sopra
/// WASAPI (NAudio, come il motore audio dell'app Windows). Stessa logica: buffer di float stereo che il motore riempie,
/// accodati e restituiti con la callback quando sono stati suonati. Cosi' MacMusicEngine (gapless, dissolvenze,
/// automix, catena DSP) gira identico.
/// Uscita esclusiva (Esclusivo / Bit perfect del motore): WASAPI in modo esclusivo, se il dispositivo accetta il
/// formato; altrimenti condiviso.
/// </summary>
internal static unsafe class WinAudioQueue
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueueBuffer { public uint Capacity; public IntPtr Data; public uint ByteSize; public IntPtr UserData; public uint PacketDescCapacity; public IntPtr PacketDescs; public uint PacketDescCount; }

    private sealed class Queue : IWaveProvider
    {
        public int Rate, Channels;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> Callback;
        public IntPtr User, Self;
        public string DeviceId = "";
        public readonly List<IntPtr> Allocated = new();
        public readonly Queue<IntPtr> Pending = new();
        public int Offset;              // byte gia' letti del primo buffer in coda
        public long SamplesPlayed;      // campioni (non fotogrammi) consegnati a WASAPI
        public WasapiOut? Out;
        public bool Running;
        public readonly object Gate = new();
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public bool Exclusive;
        /// <summary>WASAPI ha fermato l'uscita con un errore (dispositivo invalidato, preso in esclusiva): va riaperta.</summary>
        public volatile bool Broken;
        public long PausedAt;
        public int Bits = 32;           // formato in uscita (esclusivo intero a 16/24/32 bit)
        public bool Float = true;
        /// <summary>Volume attuale del motore (0..1, NaN = sconosciuto): i buffer preparati con un altro volume si correggono qui.</summary>
        public float Volume = float.NaN;

        public int Read(byte[] buffer, int offset, int count)
        {
            var done = new List<IntPtr>();
            int written = 0;
            int bytesPerSample = Bits / 8;
            lock (Gate)
            {
                while (written < count && Pending.Count > 0)
                {
                    IntPtr head = Pending.Peek();
                    var b = (QueueBuffer*)head;
                    int available = (int)b->ByteSize - Offset; // byte di float
                    if (available <= 0) { Pending.Dequeue(); Offset = 0; done.Add(head); continue; }
                    int wantSamples = (count - written) / bytesPerSample;
                    int haveSamples = available / 4;
                    int n = Math.Min(wantSamples, haveSamples);
                    if (n <= 0) break;
                    float* src = (float*)((byte*)b->Data + Offset);
                    // Volume cambiato dopo che il buffer era gia' pronto (per esempio in pausa): prima i ~270 ms gia'
                    // in coda suonavano al volume vecchio alla ripresa e il cambio arrivava "dopo".
                    if (b->PacketDescCount != 0 && !float.IsNaN(Volume))
                    {
                        float baked = BitConverter.UInt32BitsToSingle(b->PacketDescCount);
                        if (baked > 0.0001f && Math.Abs(Volume - baked) > 0.0005f)
                        {
                            float k = Math.Clamp(Volume / baked, 0f, 1f);
                            for (int i = 0; i < haveSamples; i++) src[i] *= k;
                            b->PacketDescCount = BitConverter.SingleToUInt32Bits(Math.Max(Volume, 0.00011f));
                        }
                    }
                    fixed (byte* dst0 = &buffer[offset + written]) Convert(src, dst0, n);
                    written += n * bytesPerSample;
                    Offset += n * 4;
                    SamplesPlayed += n;
                    if (Offset >= (int)b->ByteSize) { Pending.Dequeue(); Offset = 0; done.Add(head); }
                }
            }
            // Coda vuota: silenzio (WASAPI si fermerebbe se si restituisse meno del richiesto).
            if (written < count) Array.Clear(buffer, offset + written, count - written);
            if (Qa) Measure(buffer, offset, written);
            // I buffer suonati tornano al motore fuori dal lucchetto (la callback li riempie e li riaccoda).
            foreach (IntPtr b in done)
                try { Callback(User, Self, b); } catch { }
            return count;
        }

        // Prove (CINECORE_QA_SCRIPT): ogni 2 s il picco di quello che arriva davvero a WASAPI e quanti byte erano audio.
        private static readonly bool Qa = Environment.GetEnvironmentVariable("CINECORE_QA_SCRIPT") != null;
        private float _qaPeak; private long _qaBytes, _qaAudio, _qaAt;
        private void Measure(byte[] buffer, int offset, int written)
        {
            if (Float && Bits == 32)
                for (int i = 0; i + 3 < written; i += 4) _qaPeak = Math.Max(_qaPeak, Math.Abs(BitConverter.ToSingle(buffer, offset + i)));
            _qaAudio += written; _qaBytes += buffer.Length;
            long now = Environment.TickCount64;
            if (now - _qaAt < 2000) return;
            Console.Error.WriteLine($"[audio] uscita: picco={_qaPeak:0.000} audio={_qaAudio} byte, in coda={Pending.Count}");
            _qaAt = now; _qaPeak = 0; _qaAudio = 0; _qaBytes = 0;
        }

        private void Convert(float* src, byte* dst, int samples)
        {
            switch (Bits)
            {
                case 32 when Float:
                    Buffer.MemoryCopy(src, dst, samples * 4L, samples * 4L);
                    break;
                case 32:
                    for (int i = 0; i < samples; i++) ((int*)dst)[i] = (int)Math.Clamp(src[i] * 2147483647.0, int.MinValue, int.MaxValue);
                    break;
                case 24:
                    for (int i = 0; i < samples; i++)
                    {
                        int v = (int)Math.Clamp(src[i] * 8388607.0, -8388608, 8388607);
                        dst[i * 3] = (byte)v; dst[i * 3 + 1] = (byte)(v >> 8); dst[i * 3 + 2] = (byte)(v >> 16);
                    }
                    break;
                default:
                    for (int i = 0; i < samples; i++) ((short*)dst)[i] = (short)Math.Clamp(src[i] * 32767.0, -32768, 32767);
                    break;
            }
        }
    }

    private static readonly Dictionary<IntPtr, Queue> Queues = new();
    private static long _nextId = 0x5100;
    /// <summary>Dispositivi in uso esclusivo chiesto dal motore (SetHog), per id di WASAPI.</summary>
    internal static readonly HashSet<string> ExclusiveDevices = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bit chiesti per il bit perfect, per dispositivo (SetPhysicalFormat).</summary>
    internal static readonly Dictionary<string, int> DeviceBits = new(StringComparer.OrdinalIgnoreCase);

    private static Queue? Get(IntPtr q) { lock (Queues) return Queues.TryGetValue(q, out var queue) ? queue : null; }

    public static int NewOutput(double rate, int channels, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr user, out IntPtr queue)
    {
        var q = new Queue { Rate = (int)Math.Round(rate), Channels = Math.Max(1, channels), Callback = callback, User = user };
        queue = (IntPtr)Interlocked.Increment(ref _nextId);
        q.Self = queue;
        lock (Queues) Queues[queue] = q;
        return 0;
    }

    public static int AllocateBuffer(IntPtr queue, uint size, out IntPtr buffer)
    {
        buffer = IntPtr.Zero;
        if (Get(queue) is not { } q) return -50;
        IntPtr b = Marshal.AllocHGlobal(sizeof(QueueBuffer));
        *(QueueBuffer*)b = new QueueBuffer { Capacity = size, Data = Marshal.AllocHGlobal((int)size) };
        lock (q.Gate) q.Allocated.Add(b);
        buffer = b;
        return 0;
    }

    public static int Enqueue(IntPtr queue, IntPtr buffer)
    {
        if (Get(queue) is not { } q) return -50;
        lock (q.Gate) q.Pending.Enqueue(buffer);
        return 0;
    }

    public static int Start(IntPtr queue)
    {
        if (Get(queue) is not { } q) return -50;
        try
        {
            // Ripresa dopo una pausa lunga (per esempio un film, che puo' prendere l'uscita in esclusiva o in bitstream):
            // il client WASAPI di prima puo' essere stato invalidato da Windows e Play() ripartiva muto, senza errori,
            // finche' non si reimpostava l'uscita. Si riapre l'uscita; l'audio gia' in coda resta.
            if (q.Out != null && (q.Broken || q.Out.PlaybackState == PlaybackState.Paused && Environment.TickCount64 - q.PausedAt > 2000))
            {
                var stale = q.Out;
                q.Out = null;
                try { stale.Stop(); } catch { }
                try { stale.Dispose(); } catch { }
                Console.Error.WriteLine(q.Broken ? "[audio] uscita caduta: la riapro" : "[audio] ripresa dopo una pausa lunga: riapro l'uscita");
            }
            q.Broken = false;
            if (q.Out == null) Open(q);
            q.Running = true;
            q.Out!.Play();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[audio] WASAPI non avviato: " + ex.Message);
            return -66681;
        }
    }

    private static void Open(Queue q)
    {
        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = q.DeviceId.Length > 0 ? enumerator.GetDevice(q.DeviceId) : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { device = null; }
        string id = device?.ID ?? "";
        bool wantExclusive;
        lock (ExclusiveDevices) wantExclusive = id.Length > 0 && ExclusiveDevices.Contains(id);
        int bits = 32;
        lock (ExclusiveDevices) if (DeviceBits.TryGetValue(id, out int b)) bits = b;
        if (wantExclusive && device != null)
        {
            // Esclusivo: prima il formato intero chiesto (bit perfect), poi float, poi 24 e 16 bit.
            foreach (var (fmt, fbits) in ExclusiveCandidates(q.Rate, q.Channels, bits))
            {
                try
                {
                    if (!device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, fmt)) continue;
                    q.WaveFormat = fmt; q.Bits = fbits; q.Exclusive = true; q.Float = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
                    var exclusive = new WasapiOut(device, AudioClientShareMode.Exclusive, true, 60);
                    exclusive.Init(q);
                    q.Out = exclusive;
                    WatchStopped(q, exclusive);
                    Console.Error.WriteLine($"[audio] uscita esclusiva {device.FriendlyName}: {q.Rate} Hz {fbits} bit");
                    return;
                }
                catch (Exception ex) { Console.Error.WriteLine("[audio] esclusivo non riuscito: " + ex.Message); }
            }
        }
        q.Exclusive = false; q.Bits = 32; q.Float = true;
        q.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(q.Rate, q.Channels);
        var shared = device != null ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100) : new WasapiOut(AudioClientShareMode.Shared, true, 100);
        shared.Init(q);
        q.Out = shared;
        WatchStopped(q, shared);
        Console.Error.WriteLine($"[audio] WASAPI condiviso: {device?.FriendlyName ?? "predefinita"}, {q.Rate} Hz float");
    }

    /// <summary>WASAPI ferma l'uscita con un errore (dispositivo invalidato o preso in esclusiva): alla ripresa si riapre.</summary>
    private static void WatchStopped(Queue q, WasapiOut output) => output.PlaybackStopped += (_, e) =>
    {
        if (e.Exception == null || !ReferenceEquals(q.Out, output)) return;
        q.Broken = true;
        Console.Error.WriteLine("[audio] uscita fermata da Windows: " + e.Exception.Message);
    };

    private static IEnumerable<(WaveFormat, int)> ExclusiveCandidates(int rate, int channels, int bits)
    {
        if (bits is 16 or 24 or 32) yield return (new WaveFormatExtensible(rate, bits, channels), bits);
        yield return (WaveFormat.CreateIeeeFloatWaveFormat(rate, channels), 32);
        foreach (int b in new[] { 24, 32, 16 })
            if (b != bits) yield return (new WaveFormatExtensible(rate, b, channels), b);
    }

    /// <summary>Volume attuale del motore: i buffer gia' in coda vengono riscalati quando escono.</summary>
    public static void SetVolume(IntPtr queue, float volume) { if (Get(queue) is { } q) lock (q.Gate) q.Volume = volume; }

    public static int Pause(IntPtr queue)
    {
        if (Get(queue) is not { } q) return -50;
        q.Running = false;
        q.PausedAt = Environment.TickCount64;
        try { q.Out?.Pause(); } catch { }
        return 0;
    }

    public static int Stop(IntPtr queue, bool immediate)
    {
        if (Get(queue) is not { } q) return -50;
        q.Running = false;
        // Come AudioQueueStop: la riproduzione si ferma e i buffer in coda tornano liberi (il motore li riempie di nuovo
        // prima di ripartire). L'uscita si chiude: un nuovo avvio la riapre (anche su un altro dispositivo).
        var output = q.Out;
        q.Out = null;
        try { output?.Stop(); } catch { }
        try { output?.Dispose(); } catch { }
        lock (q.Gate) { q.Pending.Clear(); q.Offset = 0; q.SamplesPlayed = 0; }
        return 0;
    }

    public static int Dispose(IntPtr queue)
    {
        if (Get(queue) is not { } q) return -50;
        Stop(queue, true);
        lock (Queues) Queues.Remove(queue);
        lock (q.Gate)
        {
            foreach (IntPtr b in q.Allocated)
            {
                Marshal.FreeHGlobal(((QueueBuffer*)b)->Data);
                Marshal.FreeHGlobal(b);
            }
            q.Allocated.Clear();
        }
        return 0;
    }

    /// <summary>Campioni per canale suonati dall'avvio (AudioQueueGetCurrentTime.mSampleTime).</summary>
    public static int CurrentSampleTime(IntPtr queue, out double sampleTime)
    {
        sampleTime = 0;
        if (Get(queue) is not { } q) return -50;
        lock (q.Gate) sampleTime = q.SamplesPlayed / (double)q.Channels;
        return 0;
    }

    /// <summary>Uscita della coda (proprieta' 'aqcd'): id del dispositivo WASAPI; vuoto = predefinita.</summary>
    public static int SetDevice(IntPtr queue, string deviceId)
    {
        if (Get(queue) is not { } q) return -50;
        if (string.Equals(q.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && q.Out != null) return 0;
        q.DeviceId = deviceId;
        if (q.Out != null)
        {
            // Si cambia solo l'uscita: i buffer del motore restano in coda. Prima passava da Stop, che li svuotava
            // senza restituirli: il motore non li riempiva piu' e WASAPI suonava silenzio (musica muta all'ampli).
            bool running = q.Running;
            var output = q.Out;
            q.Out = null;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
            if (running) Start(queue);
        }
        return 0;
    }

    public static string? Device(IntPtr queue) => Get(queue)?.DeviceId;

    // ------------------------------------------------------------------ stringhe di CoreFoundation (CFString)
    public static IntPtr CFString(string text) => GCHandle.ToIntPtr(GCHandle.Alloc(text));

    public static string? CFStringValue(IntPtr cf)
    {
        try { return cf == IntPtr.Zero ? null : GCHandle.FromIntPtr(cf).Target as string; }
        catch { return null; }
    }

    public static bool CFStringGetCString(IntPtr cf, byte* buffer, long size, uint encoding)
    {
        string? s = CFStringValue(cf);
        if (s == null || size <= 0) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length + 1 > size) return false;
        Marshal.Copy(bytes, 0, (IntPtr)buffer, bytes.Length);
        buffer[bytes.Length] = 0;
        return true;
    }

    public static void CFRelease(IntPtr cf)
    {
        try { if (cf != IntPtr.Zero) GCHandle.FromIntPtr(cf).Free(); } catch { }
    }
}
