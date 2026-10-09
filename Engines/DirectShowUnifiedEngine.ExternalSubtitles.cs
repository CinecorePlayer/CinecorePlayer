#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CinecorePlayer2025.Engines
{
    // Sottotitoli esterni (.srt accanto al video o nella cartella del player) nel percorso madVR:
    // li carica XySubFilter, che li elenca prima della traccia interna in arrivo da LAV.
    // Senza file esterni per il film non cambia nulla rispetto al solo percorso LAV.
    public sealed partial class DirectShowUnifiedEngine
    {
        private const int ExternalSubtitleIndexBase = 100000;
        private readonly List<string> _externalSubtitles = new();   // nomi dati da XySubFilter, nell'ordine del filtro
        private string? _externalSubtitleBase;                      // percorso passato al filtro
        private bool _externalSubtitleSelected;

        private void LoadExternalSubtitles(string mediaPath)
        {
            _externalSubtitles.Clear();
            _externalSubtitleBase = null;
            _externalSubtitleSelected = false;
            if (_xySubFilter == null) return;
            try
            {
                // "Nascosti" si usa solo per spegnere un esterno in un film senza tracce interne:
                // non deve mai restare attivo per il film successivo.
                if (XySubtitleBridge.Hidden(_xySubFilter)) XySubtitleBridge.SetHidden(_xySubFilter, false);
                if (!File.Exists(mediaPath)) return;
                // La cartella del player ha la precedenza: e' dove finiscono i sottotitoli dei film in cartelle non scrivibili.
                if (SubtitleFile.FindInPlayerFolder(mediaPath).Count > 0)
                    _externalSubtitleBase = Path.Combine(SubtitleFile.PlayerFolder, Path.GetFileName(mediaPath));
                else if (SubtitleFile.FindNextTo(mediaPath).Count > 0)
                    _externalSubtitleBase = mediaPath;
                if (_externalSubtitleBase == null) return;
                ReloadExternalSubtitles();
                // All'apertura resta attiva la traccia interna: l'esterno si sceglie dal menu.
                DeselectExternalSubtitle();
                Dbg.Log($"[XYSUB] external subtitles: {string.Join(", ", _externalSubtitles)}", Dbg.LogLevel.Info);
            }
            catch (Exception ex) { Dbg.Warn("[XYSUB] " + ex.Message); }
        }

        private void ReloadExternalSubtitles()
        {
            if (_externalSubtitleBase == null) return;
            int embedded = XySubtitleBridge.Languages(_xySubFilter).Count - _externalSubtitles.Count;
            _externalSubtitles.Clear();
            if (!XySubtitleBridge.Load(_xySubFilter, _externalSubtitleBase)) return;
            var all = XySubtitleBridge.Languages(_xySubFilter);
            _externalSubtitles.AddRange(all.Take(Math.Max(0, all.Count - Math.Max(0, embedded))));
        }

        // Il filtro puo' rifare da solo la ricerca dei sottotitoli quando il grafo parte:
        // se i nostri file non ci sono piu', si ricaricano.
        private void EnsureExternalSubtitles()
        {
            if (_externalSubtitleBase == null || _xySubFilter == null || _externalSubtitles.Count == 0) return;
            var all = XySubtitleBridge.Languages(_xySubFilter);
            if (all.Count >= _externalSubtitles.Count && all.Take(_externalSubtitles.Count).SequenceEqual(_externalSubtitles)) return;
            Dbg.Log("[XYSUB] external subtitles dropped by the filter: reloading", Dbg.LogLevel.Info);
            bool selected = _externalSubtitleSelected;
            int index = XySubtitleBridge.Selected(_xySubFilter);
            ReloadExternalSubtitles();
            if (selected && index >= 0 && index < _externalSubtitles.Count) XySubtitleBridge.Select(_xySubFilter, index);
            else DeselectExternalSubtitle();
        }

        private void AppendExternalSubtitleStreams(List<DsStreamItem> list)
        {
            if (_externalSubtitles.Count == 0) return;
            EnsureExternalSubtitles();
            int selected = _externalSubtitleSelected && !XySubtitleBridge.Hidden(_xySubFilter) ? XySubtitleBridge.Selected(_xySubFilter) : -1;
            if (selected >= _externalSubtitles.Count) selected = -1;
            if (selected >= 0)
                foreach (var stream in list)
                    if (stream.IsSubtitle) stream.Selected = false;
            for (int i = 0; i < _externalSubtitles.Count; i++)
            {
                string name = _externalSubtitles[i];
                string? language = SubtitleNameNormalizer.TryDetectLanguageKey(name);
                list.Add(new DsStreamItem
                {
                    GlobalIndex = ExternalSubtitleIndexBase + i,
                    Group = 2,
                    LanguageKey = language,
                    IsSubtitle = true,
                    IsExternal = true,
                    Name = (string.IsNullOrWhiteSpace(name) ? "SRT" : name) + " · SRT",
                    Selected = i == selected
                });
            }
        }

        private bool SelectExternalSubtitle(int index)
        {
            EnsureExternalSubtitles();
            if (index < 0 || index >= _externalSubtitles.Count || !XySubtitleBridge.Select(_xySubFilter, index)) return false;
            _externalSubtitleSelected = true;
            _updateCb?.Invoke();
            return true;
        }

        /// <summary>Torna alla traccia interna (o a nessun sottotitolo, se il file non ne ha).</summary>
        private bool DeselectExternalSubtitle()
        {
            if (_externalSubtitles.Count == 0) return false;
            _externalSubtitleSelected = false;
            int count = XySubtitleBridge.Languages(_xySubFilter).Count;
            if (count > _externalSubtitles.Count) XySubtitleBridge.Select(_xySubFilter, _externalSubtitles.Count);
            else XySubtitleBridge.SetHidden(_xySubFilter, true);
            return true;
        }
    }
}
