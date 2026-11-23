#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecoreMac;

/// <summary>
/// Uscita semplice per l'anteprima del mix manuale (al posto di WasapiOut + BufferedWaveProvider di Windows): un anello di
/// campioni float 48 kHz stereo riempito da chi suona e svuotato da un'AudioQueue sull'uscita scelta nelle Impostazioni.
/// Con CINECORE_QA_MUTE=1 non tocca l'audio del Mac: un thread consuma i campioni in tempo reale.
/// </summary>
internal sealed unsafe class MacPcmOutput : IDisposable
{
    public const int Rate = 48000, Channels = 2;
    private const int BufferFrames = 2048, BufferCount = 3;
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CurrentDeviceProperty = 0x61716364; // 'aqcd'

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamFormat { public double SampleRate; public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueueBuffer { public uint Capacity; public IntPtr Data; public uint ByteSize; public IntPtr UserData; public uint PacketDescCapacity; public IntPtr PacketDescs; public uint PacketDescCount; }

    [DllImport(AudioToolbox)] private static extern int AudioQueueNewOutput(StreamFormat* format, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescs, IntPtr descs);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueSetProperty(IntPtr queue, uint property, IntPtr* data, uint size);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);

    private static readonly bool Mute = Environment.GetEnvironmentVariable("CINECORE_QA_MUTE") == "1";

    private readonly float[] _ring;
    private long _written, _read, _played; // campioni (non fotogrammi)
    private bool _returnedSilence;
    private readonly object _gate = new();
    private IntPtr _queue;
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly int[] _bufferSamples = new int[BufferCount];
    private GCHandle _self;
    private Thread? _muteThread;
    private volatile bool _open;

    public MacPcmOutput(double seconds = 2)
    {
        _ring = new float[(int)(seconds * Rate) * Channels];
    }

    /// <summary>Secondi in coda non ancora suonati.</summary>
    public double Buffered { get { lock (_gate) return (_written - _read) / (double)(Rate * Channels); } }
    public long PlayedFrames { get { lock (_gate) return _played / Channels; } }
    public bool Drained { get { lock (_gate) return _played == _written && (Mute || _returnedSilence); } }

    public void Write(float[] data, int samples)
    {
        lock (_gate)
            for (int i = 0; i < samples; i++)
            {
                if (_written - _read >= _ring.Length) break; // pieno: chi scrive aspetta Buffered
                _ring[_written++ % _ring.Length] = data[i];
            }
    }

    private int Pull(float* target, int samples)
    {
        lock (_gate)
        {
            int available = (int)Math.Min(samples, _written - _read);
            for (int i = 0; i < available; i++) target[i] = _ring[_read++ % _ring.Length];
            new Span<float>(target + available, samples - available).Clear();
            if (Mute) _played = _read;
            return available;
        }
    }

    public bool Start(out string? error)
    {
        error = null;
        _open = true;
        if (Mute)
        {
            _muteThread = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                long consumed = 0;
                var scratch = new float[BufferFrames * Channels];
                while (_open)
                {
                    long due = (long)(clock.Elapsed.TotalSeconds * Rate);
                    while (consumed + BufferFrames <= due) { fixed (float* p = scratch) Pull(p, scratch.Length); consumed += BufferFrames; }
                    Thread.Sleep(10);
                }
            }) { IsBackground = true, Name = "anteprima mix (muta)" };
            _muteThread.Start();
            return true;
        }
        _self = GCHandle.Alloc(this);
        var format = new StreamFormat
        {
            SampleRate = Rate, FormatId = 0x6C70636D, FormatFlags = 1 | 8,
            BytesPerPacket = 8, FramesPerPacket = 1, BytesPerFrame = 8, ChannelsPerFrame = Channels, BitsPerChannel = 32,
        };
        int r = AudioQueueNewOutput(&format, &OnBuffer, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
        if (r != 0) { error = $"AudioQueue {r}"; _queue = IntPtr.Zero; _self.Free(); _open = false; return false; }
        string uid = MacAudioDevices.ConfiguredOutputUid;
        if (uid.Length > 0)
        {
            IntPtr cf = CFStringCreateWithCString(IntPtr.Zero, uid, 0x08000100);
            AudioQueueSetProperty(_queue, CurrentDeviceProperty, &cf, (uint)IntPtr.Size);
            CFRelease(cf);
        }
        for (int i = 0; i < BufferCount; i++)
        {
            r = AudioQueueAllocateBuffer(_queue, BufferFrames * Channels * 4, out _buffers[i]);
            if (r == 0) r = Fill(i);
            if (r != 0) { error = $"AudioQueue buffer {r}"; Dispose(); return false; }
        }
        r = AudioQueueStart(_queue, IntPtr.Zero);
        if (r != 0) { error = $"AudioQueueStart {r}"; Dispose(); return false; }
        return true;
    }

    private int Fill(int index)
    {
        var bufferRef = _buffers[index];
        var b = (QueueBuffer*)bufferRef;
        _bufferSamples[index] = Pull((float*)b->Data, BufferFrames * Channels);
        b->ByteSize = BufferFrames * Channels * 4;
        return AudioQueueEnqueueBuffer(_queue, bufferRef, 0, IntPtr.Zero);
    }

    [UnmanagedCallersOnly]
    private static void OnBuffer(IntPtr user, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not MacPcmOutput output || !output._open) return;
            int index = Array.IndexOf(output._buffers, buffer);
            if (index < 0) return;
            lock (output._gate)
            {
                int samples = output._bufferSamples[index];
                output._played += samples;
                output._returnedSilence = samples == 0;
            }
            output.Fill(index);
        }
        catch { }
    }

    public void Dispose()
    {
        _open = false;
        if (_queue != IntPtr.Zero)
        {
            try { AudioQueueStop(_queue, 1); AudioQueueDispose(_queue, 1); } catch { }
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        try { _muteThread?.Join(200); } catch { }
        _muteThread = null;
    }
}
