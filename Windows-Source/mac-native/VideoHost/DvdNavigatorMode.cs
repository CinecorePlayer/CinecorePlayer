#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using DirectShowLib;
using DirectShowLib.Dvd;

namespace CinecoreVideoHost;

/// <summary>
/// DVD con il motore di Windows (DVD Navigator), come il player Windows (Engines/RenderingEngine.Dvd.cs): il Navigator
/// legge il disco (protezione CSS compresa), esegue i comandi e mostra i menu come un lettore da tavolo. Video e
/// sottoimmagini (sottotitoli e pulsanti dei menu) vanno a LAV Video, l'audio a LAV Audio, e da li' a madVR / MPC-VR.
/// Lo stato arriva dagli eventi del Navigator (mai domande bloccanti a ogni giro).
/// Righe verso l'app: "dvd &lt;dominio&gt; &lt;titolo&gt; &lt;capitolo&gt; &lt;pulsanti&gt;" quando cambia qualcosa.
/// Comandi dall'app: "dvd menu root|title|chapters|audio|subtitle", "dvd resume", "dvd move left|right|up|down",
/// "dvd activate", "dvd back", "dvd click X Y" e "dvd hover X Y" (pixel dello schermo), "dvd chapter +1|-1".
/// </summary>
internal sealed class DvdNavigatorMode
{
    private readonly IBaseFilter _navigator;
    private readonly IDvdControl2 _control;
    private readonly IDvdInfo2 _info;
    private DvdDomain _domain = DvdDomain.FirstPlay;
    private int _title, _chapter, _buttons, _titleSet;
    private double _seconds, _reported;
    private long _secondsAt;
    private int _durationTitle = -1;
    private double _duration;
    private string _lastState = "";

    public IBaseFilter Navigator => _navigator;

    /// <summary>Dallo schermo alle coordinate dell'immagine del DVD nel formato di visualizzazione (la imposta la finestra video).</summary>
    public Func<System.Drawing.Point, System.Drawing.Point?>? MapScreenPoint { get; set; }

    private DvdNavigatorMode(IBaseFilter navigator)
    {
        _navigator = navigator;
        _control = (IDvdControl2)navigator;
        _info = (IDvdInfo2)navigator;
    }

    /// <summary>Crea il Navigator sul disco (cartella con VIDEO_TS, unita' o ISO montata) e lo mette nel grafo.</summary>
    public static DvdNavigatorMode Open(IGraphBuilder graph, string root)
    {
        var navigator = (IBaseFilter)new DVDNavigator();
        DsError.ThrowExceptionForHR(graph.AddFilter(navigator, "DVD Navigator"));
        var mode = new DvdNavigatorMode(navigator);
        string folder = Directory.Exists(Path.Combine(root, "VIDEO_TS")) ? Path.Combine(root, "VIDEO_TS") : root;
        DsError.ThrowExceptionForHR(mode._control.SetDVDDirectory(folder));
        mode._control.SetOption(DvdOptionFlag.HMSFTimeCodeEvents, true);
        mode._control.SetOption(DvdOptionFlag.ResetOnStop, false);
        try
        {
            mode._control.SelectDefaultMenuLanguage(0x0410);
            mode._control.SelectDefaultAudioLanguage(0x0410, DvdAudioLangExt.NotSpecified);
        }
        catch { }
        Out.Line("info DVD Navigator aperto su " + folder);
        return mode;
    }

    private static IPin? PinByName(IBaseFilter filter, PinDirection direction, string fragment)
    {
        filter.EnumPins(out IEnumPins pins);
        try
        {
            var one = new IPin[1];
            while (pins.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].QueryDirection(out PinDirection dir);
                one[0].QueryPinInfo(out PinInfo info);
                try { if (info.filter != null) Marshal.ReleaseComObject(info.filter); } catch { }
                if (dir == direction && (info.name ?? "").Contains(fragment, StringComparison.OrdinalIgnoreCase)) return one[0];
                Marshal.ReleaseComObject(one[0]);
            }
            return null;
        }
        finally { Marshal.ReleaseComObject(pins); }
    }

    /// <summary>
    /// Navigator -> LAV Video (video e, sul secondo ingresso, le sottoimmagini) e Navigator -> LAV Audio. Restituisce le
    /// uscite dei decodificatori da collegare ai renderer gia' nel grafo.
    /// </summary>
    public IPin[] Connect(IGraphBuilder graph, IBaseFilter lavVideo, IBaseFilter lavAudio, bool noAudio)
    {
        IPin video = PinByName(_navigator, PinDirection.Output, "Video") ?? throw new ApplicationException("DVD Navigator: pin video mancante");
        IPin videoIn = PinByName(lavVideo, PinDirection.Input, "Input") ?? DsFindPin.ByDirection(lavVideo, PinDirection.Input, 0);
        DsError.ThrowExceptionForHR(graph.ConnectDirect(video, videoIn, null));
        IPin? subpicture = PinByName(_navigator, PinDirection.Output, "SubPicture");
        IPin? subtitleIn = PinByName(lavVideo, PinDirection.Input, "Subtitle");
        if (subpicture != null && subtitleIn != null)
        {
            int hr = graph.ConnectDirect(subpicture, subtitleIn, null);
            Out.Line(hr < 0 ? $"info DVD: sottoimmagini non collegate (0x{hr:X8}): menu senza evidenziazione" : "info DVD: video e sottoimmagini a LAV Video");
        }
        var outputs = new System.Collections.Generic.List<IPin> { DsFindPin.ByDirection(lavVideo, PinDirection.Output, 0) };
        if (!noAudio && (PinByName(_navigator, PinDirection.Output, "AC3") ?? PinByName(_navigator, PinDirection.Output, "Audio")) is { } audio)
        {
            IPin audioIn = DsFindPin.ByDirection(lavAudio, PinDirection.Input, 0);
            int hr = graph.ConnectDirect(audio, audioIn, null);
            if (hr >= 0) outputs.Add(DsFindPin.ByDirection(lavAudio, PinDirection.Output, 0));
            else Out.Line($"info DVD: audio non collegato (0x{hr:X8})");
        }
        return outputs.ToArray();
    }

    /// <summary>Eventi del grafo (EC_DVD_*).</summary>
    public void HandleEvent(EventCode code, IntPtr param1, IntPtr param2)
    {
        long p1 = param1.ToInt64();
        switch (code)
        {
            case EventCode.DvdDomainChange: _domain = (DvdDomain)(int)p1; _buttons = 0; _seconds = 0; _secondsAt = 0; _reported = 0; _durationTitle = -2; break;
            case EventCode.DvdTitleChange: _title = (int)p1; _buttons = 0; _chapter = 1; _seconds = 0; _secondsAt = 0; _reported = 0; break;
            case EventCode.DvdChapterStart: _chapter = (int)p1; break;
            case EventCode.DvdTitleSetChange: _titleSet = (int)p1; break;
            case EventCode.DvdButtonChange: _buttons = (int)p1; break;
            case EventCode.DvdCurrentHmsfTime:
                {
                    uint packed = (uint)p1;
                    int flags = (int)param2.ToInt64();
                    double fps = (flags & (int)DvdTimeCodeFlags.FPS30) != 0 ? 30 : 25;
                    _seconds = (packed & 0xFF) * 3600 + ((packed >> 8) & 0xFF) * 60 + ((packed >> 16) & 0xFF) + ((packed >> 24) & 0xFF) / fps;
                    _secondsAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    break;
                }
            case EventCode.DvdPlaybackStopped: _domain = DvdDomain.Stop; break;
        }
    }

    public bool InTitle => _domain == DvdDomain.Title && _title > 0;

    // Avvertenze e loghi prima del menu (FirstPlay): anche li' avanzamento e durata, come il player Windows.
    private bool Timed => InTitle || _domain == DvdDomain.FirstPlay;

    public double Position(bool playing)
    {
        if (!Timed) { _reported = 0; return 0; }
        double seconds = _seconds;
        if (_secondsAt != 0 && playing) seconds += Math.Min(1.0, System.Diagnostics.Stopwatch.GetElapsedTime(_secondsAt).TotalSeconds);
        if (seconds < _reported && _reported - seconds < 1.5) seconds = _reported;
        return _reported = seconds;
    }

    public double Duration()
    {
        if (!Timed) return 0;
        int key = InTitle ? _title : -1;
        if (_durationTitle == key && _duration > 0) return _duration;
        _durationTitle = key; _duration = 0;
        try
        {
            var total = new DvdHMSFTimeCode();
            if (_info.GetTotalTitleTime(total, out DvdTimeCodeFlags flags) >= 0)
                _duration = total.bHours * 3600 + total.bMinutes * 60 + total.bSeconds + total.bFrames / (flags.HasFlag(DvdTimeCodeFlags.FPS30) ? 30.0 : 25.0);
        }
        catch { }
        return _duration;
    }

    /// <summary>Riga di stato per l'app, solo quando cambia.</summary>
    public void ReportState()
    {
        string domain = _domain switch
        {
            DvdDomain.Title => "title", DvdDomain.VideoManagerMenu => "menu", DvdDomain.VideoTitleSetMenu => "menu",
            DvdDomain.FirstPlay => "start", DvdDomain.Stop => "stop", _ => "other",
        };
        string state = $"dvd {domain} {_title} {_chapter} {_buttons} {_titleSet}";
        if (state == _lastState) return;
        _lastState = state;
        Out.Line(state);
    }

    public void Seek(double seconds)
    {
        if (!InTitle) return;
        seconds = Math.Max(0, seconds);
        int whole = (int)seconds;
        var time = new DvdHMSFTimeCode { bHours = (byte)(whole / 3600), bMinutes = (byte)(whole / 60 % 60), bSeconds = (byte)(whole % 60) };
        int hr = _control.PlayAtTime(time, DvdCmdFlags.Flush, out IDvdCmd? command);
        Release(command);
        if (hr >= 0) { _seconds = seconds; _secondsAt = 0; _reported = seconds; }
        else Out.Line($"info DVD: salto rifiutato (0x{hr:X8})");
    }

    private static void Release(IDvdCmd? command) { if (command != null) try { Marshal.ReleaseComObject(command); } catch { } }
    private static int Done(int hr, IDvdCmd? command) { Release(command); return hr; }

    /// <summary>Comando "dvd ..." dall'app.</summary>
    public void Command(string[] p)
    {
        if (p.Length < 2) return;
        int hr = 0;
        try
        {
            switch (p[1])
            {
                case "menu":
                    var id = (p.Length > 2 ? p[2] : "root") switch
                    {
                        "title" => DvdMenuId.Title, "chapters" => DvdMenuId.Chapter, "audio" => DvdMenuId.Audio,
                        "subtitle" => DvdMenuId.Subpicture, _ => DvdMenuId.Root,
                    };
                    hr = Done(_control.ShowMenu(id, DvdCmdFlags.Flush, out IDvdCmd? m), m);
                    if (hr < 0 && id == DvdMenuId.Root) hr = Done(_control.ShowMenu(DvdMenuId.Title, DvdCmdFlags.Flush, out IDvdCmd? t), t);
                    break;
                case "resume": hr = Done(_control.Resume(DvdCmdFlags.Flush, out IDvdCmd? r), r); break;
                case "move":
                    hr = _control.SelectRelativeButton(p.Length > 2 ? p[2] switch
                    {
                        "left" => DvdRelativeButton.Left, "right" => DvdRelativeButton.Right, "up" => DvdRelativeButton.Upper, _ => DvdRelativeButton.Lower,
                    } : DvdRelativeButton.Lower);
                    break;
                case "activate": hr = _control.ActivateButton(); break;
                case "back": hr = Done(_control.ReturnFromSubmenu(DvdCmdFlags.Flush, out IDvdCmd? b), b); break;
                case "click":
                case "hover":
                    if (p.Length < 4) return;
                    var screen = new System.Drawing.Point(int.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture));
                    // Il Navigator vuole il punto nell'immagine (formato di visualizzazione), non sullo schermo: lo converte la finestra
                    // video. Fuori dall'immagine non si fa nulla.
                    if (MapScreenPoint?.Invoke(screen) is not { } point) { if (p[1] == "click") Out.Line("dvd clicked 0"); return; }
                    hr = p[1] == "click" ? _control.ActivateAtPosition(point) : _control.SelectAtPosition(point);
                    if (p[1] == "click") Out.Line(hr >= 0 ? "dvd clicked 1" : "dvd clicked 0");
                    return;
                case "chapter":
                    hr = p.Length > 2 && p[2].StartsWith('-')
                        ? Done(_control.PlayPrevChapter(DvdCmdFlags.Flush, out IDvdCmd? prev), prev)
                        : Done(_control.PlayNextChapter(DvdCmdFlags.Flush, out IDvdCmd? next), next);
                    break;
            }
            if (hr < 0) Out.Line($"info DVD: {p[1]} rifiutato (0x{hr:X8})");
        }
        catch (Exception ex) { Out.Line("info DVD: " + p[1] + ": " + ex.Message); }
    }
}
