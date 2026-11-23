using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CinecoreMac;

/// <summary>
/// Lettore di riserva della musica (al posto di AVPlayer del Mac): Media Foundation decodifica, WASAPI suona
/// sull'uscita scelta. Si usa solo se il motore audio di Cinecore e' spento o non parte.
/// </summary>
internal sealed class MacAudioPlayer : IDisposable
{
    private MediaFoundationReader? _reader;
    private WasapiOut? _out;
    private float _volume = 1f;
    private bool _muted, _failed;
    private string _deviceId = "";

    public string? Source { get; private set; }

    public void Open(string pathOrUrl)
    {
        Stop();
        Source = pathOrUrl;
        _failed = false;
        try
        {
            _reader = new MediaFoundationReader(pathOrUrl);
            _out = CreateOutput();
            _out.Init(_reader);
            ApplyVolume();
            _out.Play();
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.Error.WriteLine("[musica] lettore di riserva: " + ex.Message);
        }
    }

    private WasapiOut CreateOutput()
    {
        _deviceId = MacAudioDevices.ConfiguredOutputUid;
        if (_deviceId.Length > 0)
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return new WasapiOut(enumerator.GetDevice(_deviceId), AudioClientShareMode.Shared, true, 100);
            }
            catch { }
        return new WasapiOut(AudioClientShareMode.Shared, true, 100);
    }

    public void SetOutput(string uid)
    {
        if (_reader == null || uid == _deviceId) return;
        bool playing = IsPlaying;
        var reader = _reader;
        _out?.Dispose();
        _out = CreateOutput();
        _out.Init(reader);
        ApplyVolume();
        if (playing) _out.Play();
    }

    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0f, 1f); ApplyVolume(); } }
    public bool Muted { get => _muted; set { _muted = value; ApplyVolume(); } }

    private void ApplyVolume()
    {
        try { if (_out != null) _out.Volume = _muted ? 0f : _volume; } catch { }
    }

    public void Play() { try { _out?.Play(); } catch { } }
    public void Pause() { try { _out?.Pause(); } catch { } }
    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    public double Position
    {
        get { try { return _reader?.CurrentTime.TotalSeconds ?? 0; } catch { return 0; } }
    }

    public double Duration
    {
        get { try { return _reader?.TotalTime.TotalSeconds ?? 0; } catch { return 0; } }
    }

    public bool Failed => _failed;

    public void Seek(double seconds)
    {
        try { if (_reader != null) _reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, seconds)); } catch { }
    }

    public void Stop()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _reader?.Dispose(); _reader = null;
    }

    public void Dispose() => Stop();
}
