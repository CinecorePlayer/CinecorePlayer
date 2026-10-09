#nullable enable
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using DirectShowLib.Dvd;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Engines
{
    // DVD con il motore di Windows (DVD Navigator): e' lui a leggere il disco, a eseguirne i comandi e a
    // mostrare i menu, come un lettore da tavolo. Video e sottoimmagini (sottotitoli e pulsanti dei menu)
    // vanno a LAV Video, l'audio a LAV Audio, e da li' ai renderer scelti: madVR, MPC Video Renderer, EVR.
    // La protezione del disco la negoziano il Navigator e i decodificatori; richiede renderer veri in fondo
    // alla catena e la regione del lettore uguale a quella del disco.
    // Tempo, durata, capitoli e tracce non passano da IMediaSeeking ne' da IAMStreamSelect come per i file:
    // si chiedono al Navigator (IDvdInfo2) e si comandano con IDvdControl2.
    public sealed partial class DirectShowUnifiedEngine
    {
        private IBaseFilter? _dvdNavigator;
        private IDvdControl2? _dvdControl;
        private IDvdInfo2? _dvdInfo;

        /// <summary>Il disco e' aperto con il motore DVD di Windows (menu e navigazione disponibili).</summary>
        public bool DvdMode => _dvdControl != null && _dvdInfo != null;

        /// <summary>Questo disco puo' essere aperto dal motore DVD di Windows (DVD in un lettore o in una cartella, lettura completa scelta).</summary>
        internal static bool DvdNavigatorCanOpen(DiscMedia.Source disc) => IsDvdForNavigator(disc);

        private static bool IsDvdForNavigator(DiscMedia.Source disc) =>
            DvdPlaybackPreference.FullPlayer && disc.Kind == DiscKind.Dvd && !disc.IsImage &&
            Directory.Exists(Path.Combine(disc.Device, "VIDEO_TS")) &&
            !string.Equals(_dvdRefusedDevice, disc.Device, StringComparison.OrdinalIgnoreCase);

        // Disco che il motore di Windows non ha voluto riprodurre in questa sessione (regione diversa da quella
        // del lettore, protezione non negoziata): le aperture successive vanno dritte a mpv.
        private static string? _dvdRefusedDevice;
        private string? _dvdDevice;

        /// <summary>
        /// Dopo l'avvio: il Navigator e' fermo e non mostra ne' un titolo ne' un menu. Chi apre il disco lo
        /// chiama se il film non parte; da quel momento il disco viene lasciato a mpv.
        /// </summary>
        public bool DvdGaveUp()
        {
            if (!DvdMode || DvdDomainNow() != DvdDomain.Stop) return false;
            _dvdRefusedDevice = _dvdDevice;
            Dbg.Warn("[DVD] the Windows DVD engine did not start this disc (region or copy protection): it will be opened with mpv");
            return true;
        }

        /// <summary>Crea il Navigator sul disco e lo mette nel grafo al posto dello splitter.</summary>
        private void OpenDvdNavigator(DiscMedia.Source disc)
        {
            var navigator = (IBaseFilter)new DVDNavigator();
            DsError.ThrowExceptionForHR(_graph!.AddFilter(navigator, "DVD Navigator"));
            var control = (IDvdControl2)navigator;
            DsError.ThrowExceptionForHR(control.SetDVDDirectory(Path.Combine(disc.Device, "VIDEO_TS")));
            // Tempi in ore/minuti/secondi/fotogrammi; lo stop del grafo non riporta il disco all'inizio.
            control.SetOption(DvdOptionFlag.HMSFTimeCodeEvents, true);
            control.SetOption(DvdOptionFlag.ResetOnStop, false);
            // Menu e audio nella lingua dell'interfaccia, se il disco l'ha (altrimenti decide il disco).
            try
            {
                int language = AppLanguage.English ? 0x0409 : 0x0410;
                control.SelectDefaultMenuLanguage(language);
                control.SelectDefaultAudioLanguage(language, DvdAudioLangExt.NotSpecified);
            }
            catch (Exception ex) { Dbg.Log("[DVD] default language: " + ex.Message, Dbg.LogLevel.Info); }
            _dvdNavigator = navigator;
            _dvdControl = control;
            _dvdInfo = (IDvdInfo2)navigator;
            _lavSource = navigator;
            _dvdDevice = disc.Device;
            Dbg.Log($"[DVD] navigator opened on '{disc.Device}'", Dbg.LogLevel.Info);
        }

        private static IPin? FindPinByName(IBaseFilter filter, PinDirection direction, string fragment)
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

        /// <summary>Navigator -> LAV Video: il video e, sul secondo ingresso, le sottoimmagini (sottotitoli, pulsanti dei menu).</summary>
        private void ConnectDvdVideoPins()
        {
            IPin video = FindPinByName(_dvdNavigator!, PinDirection.Output, "Video") ?? throw new ApplicationException("DVD Navigator: pin video mancante");
            IPin decoderIn = FindPinByName(_lavVideo!, PinDirection.Input, "Input") ?? FindPin(_lavVideo!, PinDirection.Input, null) ?? throw new ApplicationException("LAV Video: ingresso mancante");
            DsError.ThrowExceptionForHR(_graph!.ConnectDirect(video, decoderIn, null));
            IPin? subpicture = FindPinByName(_dvdNavigator!, PinDirection.Output, "SubPicture");
            IPin? subtitleIn = FindPinByName(_lavVideo!, PinDirection.Input, "Subtitle");
            if (subpicture != null && subtitleIn != null)
            {
                int hr = _graph.ConnectDirect(subpicture, subtitleIn, null);
                if (hr < 0) Dbg.Warn($"[DVD] subpicture pin not connected (0x{hr:X8}): menus will have no highlight");
            }
            else Dbg.Warn("[DVD] no subpicture path: menus will have no highlight");
            Dbg.Log("[DVD] navigator -> LAV Video (video + subpicture)", Dbg.LogLevel.Info);
        }

        private IPin? DvdAudioPin() => _dvdNavigator == null ? null
            : FindPinByName(_dvdNavigator, PinDirection.Output, "AC3") ?? FindPinByName(_dvdNavigator, PinDirection.Output, "Audio");

        private static double Seconds(DvdHMSFTimeCode time, double framesPerSecond) =>
            time.bHours * 3600 + time.bMinutes * 60 + time.bSeconds + (framesPerSecond > 0 ? time.bFrames / framesPerSecond : 0);

        private static DvdHMSFTimeCode TimeCode(double seconds)
        {
            seconds = Math.Max(0, seconds);
            int whole = (int)seconds;
            return new DvdHMSFTimeCode { bHours = (byte)(whole / 3600), bMinutes = (byte)(whole / 60 % 60), bSeconds = (byte)(whole % 60), bFrames = 0 };
        }

        private DvdDomain DvdDomainNow()
        {
            try { if (_dvdInfo != null && _dvdInfo.GetCurrentDomain(out DvdDomain domain) >= 0) return domain; } catch { }
            return DvdDomain.Stop;
        }

        /// <summary>Si sta guardando un menu del disco (o una schermata con pulsanti): frecce e OK vanno al disco.</summary>
        public bool DvdMenuActive
        {
            get
            {
                if (!DvdMode) return false;
                try
                {
                    DvdDomain domain = DvdDomainNow();
                    if (domain is DvdDomain.VideoManagerMenu or DvdDomain.VideoTitleSetMenu) return true;
                    return _dvdInfo!.GetCurrentButton(out int buttons, out _) >= 0 && buttons > 0;
                }
                catch { return false; }
            }
        }

        private double DvdDuration()
        {
            try
            {
                var total = new DvdHMSFTimeCode();
                if (_dvdInfo!.GetTotalTitleTime(total, out DvdTimeCodeFlags flags) >= 0)
                    return Seconds(total, flags.HasFlag(DvdTimeCodeFlags.FPS30) ? 30 : 25);
            }
            catch { }
            return 0;
        }

        private double DvdPosition()
        {
            try
            {
                if (_dvdInfo!.GetCurrentLocation(out DvdPlaybackLocation2 location) >= 0)
                    return Seconds(location.TimeCode, ((int)location.TimeCodeFlags & (int)DvdTimeCodeFlags.FPS30) != 0 ? 30 : 25);
            }
            catch { }
            return 0;
        }

        private void DvdSeek(double seconds)
        {
            try
            {
                // Solo dentro un titolo: in un menu non c'e' un tempo a cui saltare.
                if (DvdDomainNow() != DvdDomain.Title) return;
                int hr = _dvdControl!.PlayAtTime(TimeCode(seconds), DvdCmdFlags.Flush, out IDvdCmd? command);
                if (command != null) Marshal.ReleaseComObject(command);
                if (hr < 0) Dbg.Log($"[DVD] seek to {seconds:0}s refused (0x{hr:X8})", Dbg.LogLevel.Info);
            }
            catch (Exception ex) { Dbg.Warn("[DVD] seek: " + ex.Message); }
        }

        private static string LanguageName(int lcid)
        {
            try { if (lcid > 0) return CultureInfo.GetCultureInfo(lcid).TwoLetterISOLanguageName; } catch { }
            return "";
        }

        private List<DsStreamItem> DvdStreams()
        {
            var list = new List<DsStreamItem>();
            try
            {
                if (_dvdInfo!.GetCurrentAudio(out int audioCount, out int currentAudio) >= 0)
                {
                    for (int i = 0; i < audioCount; i++)
                    {
                        _dvdInfo.GetAudioLanguage(i, out int lcid);
                        string language = LanguageName(lcid);
                        string detail = "";
                        try
                        {
                            if (_dvdInfo.GetAudioAttributes(i, out DvdAudioAttributes attributes) >= 0)
                                detail = $"{attributes.AudioFormat} {attributes.bNumberOfChannels}ch".Replace("AC3", "Dolby Digital");
                        }
                        catch { }
                        list.Add(new DsStreamItem
                        {
                            GlobalIndex = i, NativeIndex = i, IsAudio = true, Group = 1,
                            LanguageKey = SubtitleNameNormalizer.TryDetectLanguageKey(language),
                            Name = $"Audio #{i + 1}" + (language.Length > 0 ? " - " + language : "") + (detail.Length > 0 ? " - " + detail : ""),
                            Selected = i == currentAudio
                        });
                    }
                }
                if (_dvdInfo.GetCurrentSubpicture(out int subCount, out int currentSub, out bool disabled) >= 0)
                {
                    for (int i = 0; i < subCount; i++)
                    {
                        _dvdInfo.GetSubpictureLanguage(i, out int lcid);
                        string language = LanguageName(lcid);
                        list.Add(new DsStreamItem
                        {
                            GlobalIndex = 1000 + i, NativeIndex = i, IsSubtitle = true, Group = 2,
                            LanguageKey = SubtitleNameNormalizer.TryDetectLanguageKey(language),
                            Name = $"Sub #{i + 1}" + (language.Length > 0 ? " - " + language : ""),
                            Selected = !disabled && i == currentSub
                        });
                    }
                }
            }
            catch (Exception ex) { Dbg.Warn("[DVD] streams: " + ex.Message); }
            return list;
        }

        private bool DvdEnableStream(int globalIndex)
        {
            try
            {
                IDvdCmd? command;
                int hr;
                if (globalIndex >= 1000)
                {
                    hr = _dvdControl!.SelectSubpictureStream(globalIndex - 1000, DvdCmdFlags.None, out command);
                    if (command != null) Marshal.ReleaseComObject(command);
                    if (hr >= 0) { _dvdControl.SetSubpictureState(true, DvdCmdFlags.None, out command); if (command != null) Marshal.ReleaseComObject(command); }
                }
                else
                {
                    hr = _dvdControl!.SelectAudioStream(globalIndex, DvdCmdFlags.None, out command);
                    if (command != null) Marshal.ReleaseComObject(command);
                }
                if (hr < 0) Dbg.Log($"[DVD] stream {globalIndex} refused (0x{hr:X8})", Dbg.LogLevel.Info);
                _updateCb?.Invoke();
                return hr >= 0;
            }
            catch (Exception ex) { Dbg.Warn("[DVD] select stream: " + ex.Message); return false; }
        }

        private bool DvdSubtitlesOff()
        {
            try
            {
                int hr = _dvdControl!.SetSubpictureState(false, DvdCmdFlags.None, out IDvdCmd? command);
                if (command != null) Marshal.ReleaseComObject(command);
                return hr >= 0;
            }
            catch { return false; }
        }

        // ----- comandi da lettore -----

        private bool DvdCommand(string what, Func<IDvdControl2, int> action)
        {
            if (!DvdMode) return false;
            try
            {
                int hr = action(_dvdControl!);
                if (hr < 0) Dbg.Log($"[DVD] {what} refused (0x{hr:X8})", Dbg.LogLevel.Info);
                return hr >= 0;
            }
            catch (Exception ex) { Dbg.Warn($"[DVD] {what}: {ex.Message}"); return false; }
        }

        private static int Done(int hr, IDvdCmd? command)
        {
            if (command != null) { try { Marshal.ReleaseComObject(command); } catch { } }
            return hr;
        }

        /// <summary>Menu principale del disco.</summary>
        public bool DvdShowRootMenu() => DvdCommand("root menu", c => Done(c.ShowMenu(DvdMenuId.Root, DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd))
                                         || DvdCommand("title menu", c => Done(c.ShowMenu(DvdMenuId.Title, DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd));

        /// <summary>Dal menu torna al film, nel punto in cui era.</summary>
        public bool DvdResumeFromMenu() => DvdCommand("resume", c => Done(c.Resume(DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd));

        /// <summary>Sposta la selezione fra i pulsanti del menu: -1/+1 in orizzontale, -1/+1 in verticale.</summary>
        public bool DvdMoveSelection(int horizontal, int vertical) => DvdCommand("move", c =>
            c.SelectRelativeButton(horizontal < 0 ? DvdRelativeButton.Left : horizontal > 0 ? DvdRelativeButton.Right : vertical < 0 ? DvdRelativeButton.Upper : DvdRelativeButton.Lower));

        public bool DvdActivate() => DvdCommand("activate", c => c.ActivateButton());

        public bool DvdBack() => DvdCommand("back", c => Done(c.ReturnFromSubmenu(DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd));

        /// <summary>Capitolo successivo (+1) o precedente (-1) del titolo in corso.</summary>
        public bool DvdStepChapter(int direction) => DvdCommand("chapter", c => direction > 0
            ? Done(c.PlayNextChapter(DvdCmdFlags.Flush, out IDvdCmd? next), next)
            : Done(c.PlayPrevChapter(DvdCmdFlags.Flush, out IDvdCmd? previous), previous));

        public bool DvdPlayChapter(int chapter) => DvdCommand("play chapter", c => Done(c.PlayChapter(chapter, DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd));

        /// <summary>Titolo e capitolo in corso e quanti capitoli ha il titolo (0 nei menu).</summary>
        public (int Title, int Chapter, int Chapters) DvdLocation()
        {
            try
            {
                if (DvdMode && _dvdInfo!.GetCurrentLocation(out DvdPlaybackLocation2 location) >= 0 && location.TitleNum > 0 &&
                    _dvdInfo.GetNumberOfChapters(location.TitleNum, out int chapters) >= 0)
                    return (location.TitleNum, location.ChapterNum, chapters);
            }
            catch { }
            return (0, 0, 0);
        }

        /// <summary>Riprende il film da un punto salvato: titolo e secondi. Il disco puo' rifiutare finche' mostra avvisi iniziali.</summary>
        public bool DvdPlayTitleAt(int title, double seconds) => DvdCommand("resume at time", c =>
            Done(c.PlayAtTimeInTitle(title, TimeCode(seconds), DvdCmdFlags.Flush, out IDvdCmd? cmd), cmd));

        /// <summary>Clic del mouse su un pulsante del menu, in coordinate del riquadro video.</summary>
        public bool DvdClickAt(System.Drawing.Point videoPoint) => DvdCommand("click", c => c.ActivateAtPosition(videoPoint));

        public bool DvdHoverAt(System.Drawing.Point videoPoint)
        {
            if (!DvdMode) return false;
            try { return _dvdControl!.SelectAtPosition(videoPoint) >= 0; } catch { return false; }
        }

        // Rapporto del quadro a schermo (16:9 o 4:3), diverso da quello dei 720 pixel memorizzati.
        private double _dvdDisplayAspect;

        /// <summary>Formato del video del disco, per il pannello Info e per i calcoli che servono i fotogrammi al secondo.</summary>
        public MediaProbe.Result DescribeDvd()
        {
            var info = new MediaProbe.Result { HasVideo = true, Format = "dvd", Duration = DvdDuration() };
            try
            {
                if (_dvdInfo!.GetCurrentVideoAttributes(out DvdVideoAttributes video) >= 0)
                {
                    info.Width = video.sourceResolutionX;
                    info.Height = video.sourceResolutionY;
                    info.VideoFps = video.frameRate == 50 ? 25 : video.frameRate == 60 ? 30000.0 / 1001.0 : 25;
                    // Il DVD e' anamorfico: 720 pixel per un quadro 16:9 o 4:3. Senza questo rapporto il riquadro
                    // veniva calcolato come 5:4 e il film restava piu' piccolo dello schermo.
                    if (video.aspectX > 0 && video.aspectY > 0 && info.Width > 0 && info.Height > 0)
                    {
                        info.SampleAspect = (video.aspectX / (double)video.aspectY) / (info.Width / (double)info.Height);
                        double aspect = video.aspectX / (double)video.aspectY;
                        if (Math.Abs(aspect - _dvdDisplayAspect) > 0.001)
                        {
                            // Menu 4:3 e film 16:9 sullo stesso disco: il riquadro va ricalcolato.
                            _dvdDisplayAspect = aspect;
                            _appliedMpcvrPlacement = null;
                            _appliedMadVrPlacement = null;
                        }
                    }
                }
            }
            catch { }
            return info;
        }

        private void ReleaseDvd()
        {
            _dvdControl = null;
            _dvdInfo = null;
            // Il filtro e' lo stesso oggetto di _lavSource: lo rilascia la chiusura del grafo.
            _dvdNavigator = null;
            _dvdDisplayAspect = 0;
        }
    }

    /// <summary>
    /// Come leggere i DVD: lettore completo (menu del disco, avvisi iniziali, navigazione, tutti i renderer
    /// DirectShow) oppure diretto al film con mpv, senza menu. Il lettore completo richiede che la regione del
    /// lettore corrisponda a quella del disco; se il disco non parte si ripiega da soli sulla lettura diretta.
    /// </summary>
    internal static class DvdPlaybackPreference
    {
        private static bool? _full;
        private static string SettingPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "dvd-playback.txt");

        public static bool FullPlayer
        {
            get
            {
                if (_full.HasValue) return _full.Value;
                try { _full = !File.Exists(SettingPath) || File.ReadAllText(SettingPath).Trim() != "direct"; }
                catch { _full = true; }
                return _full.Value;
            }
            set
            {
                _full = value;
                try { Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!); File.WriteAllText(SettingPath, value ? "full" : "direct"); }
                catch (Exception ex) { Dbg.Warn("[DVD] setting: " + ex.Message); }
            }
        }
    }
}
