#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Rapporto della libreria video: cosa c'e' (quanti titoli, quanto spazio, quali formati)
    /// e cosa merita un'occhiata: doppioni, risoluzione o bitrate bassi, HDR con metadati di
    /// luminosita' assenti o incoerenti, file che non si aprono. Si legge solo cio' che ogni
    /// file dichiara (nessuna decodifica): pochi decimi di secondo a titolo.
    /// </summary>
    internal static class LibraryReport
    {
        public sealed record Source(string Path, string Title, int? Year);

        public sealed record Finding(string Title, string Detail, string Path);

        public sealed record Section(string Key, List<Finding> Findings);

        public sealed class Result
        {
            public int Titles { get; set; }
            public long Bytes { get; set; }
            public int Uhd { get; set; }
            public int FullHd { get; set; }
            public int Hd { get; set; }
            public int Sd { get; set; }
            public int Hdr { get; set; }
            public int DolbyVision { get; set; }
            public List<Section> Sections { get; set; } = new();
        }

        private sealed record Entry(Source Source, string Key, string Label, long Bytes, HdrAnalyzer.Result Declared, HdrAnalyzer.Result? Measured);

        public static string Size(long bytes)
        {
            double gb = bytes / 1024d / 1024d / 1024d;
            return gb >= 1000 ? (gb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " TB"
                : gb >= 1 ? gb.ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                : (bytes / 1024d / 1024d).ToString("0", CultureInfo.CurrentCulture) + " MB";
        }

        private static string Format(Entry entry)
        {
            var d = entry.Declared;
            string resolution = d.Width >= 3000 ? "4K" : d.Width >= 1800 ? "1080p" : d.Width >= 1200 ? "720p" : d.Width > 0 ? d.Width + "×" + d.Height : "?";
            return resolution + (d.IsHdr ? " HDR" : "");
        }

        public static Result Build(IReadOnlyList<Source> sources, bool english, Action<double>? progress, CancellationToken ct)
        {
            string T(string italian, string englishText) => global::CinecorePlayer2025.Utilities.AppLanguage.Localize(english ? englishText : italian);
            var result = new Result();
            var entries = new List<Entry>();
            var unreadable = new List<Finding>();

            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var source = sources[i];
                progress?.Invoke(i / (double)Math.Max(1, sources.Count));
                long bytes;
                try
                {
                    var info = new FileInfo(source.Path);
                    if (!info.Exists) continue; // disco scollegato o file spostato: non e' un difetto del file
                    bytes = info.Length;
                }
                catch { continue; }

                var declared = HdrAnalyzer.ReadDeclared(source.Path);
                if (declared.Error != null || declared.Width <= 0)
                {
                    unreadable.Add(new Finding(source.Title, Size(bytes) + "  ·  " + System.IO.Path.GetFileName(source.Path), source.Path));
                    continue;
                }

                // Episodi: la chiave comprende stagione ed episodio, altrimenti una serie intera
                // risulterebbe un unico titolo ripetuto.
                string key, label = source.Title;
                try
                {
                    var parsed = MovieMetadataService.ExtractMediaTitleInfoFromPath(source.Path);
                    if (parsed.IsTvEpisode && parsed.SeasonNumber.HasValue && parsed.EpisodeNumber.HasValue)
                    {
                        string episode = $"S{parsed.SeasonNumber:00}E{parsed.EpisodeNumber:00}";
                        string series = string.IsNullOrWhiteSpace(parsed.SeriesTitle) ? source.Title : parsed.SeriesTitle!;
                        key = MusicArtistArtworkService.Identity(series) + "|" + episode;
                        label = series + "  " + episode;
                    }
                    else
                        key = MusicArtistArtworkService.Identity(source.Title) + "|" + (source.Year ?? parsed.Year)?.ToString(CultureInfo.InvariantCulture);
                }
                catch { key = MusicArtistArtworkService.Identity(source.Title) + "|" + source.Year; }

                HdrAnalyzer.Result? measured = declared.IsHdr ? HdrAnalyzer.TryLoadCached(source.Path, 1) : null;
                entries.Add(new Entry(source, key, label, bytes, declared, measured));

                result.Titles++;
                result.Bytes += bytes;
                if (declared.Width >= 3000) result.Uhd++;
                else if (declared.Width >= 1800) result.FullHd++;
                else if (declared.Width >= 1200) result.Hd++;
                else result.Sd++;
                if (declared.IsHdr) result.Hdr++;
                if (declared.DolbyVisionProfile != null) result.DolbyVision++;
            }
            progress?.Invoke(1);

            // Doppioni: stesso titolo (e anno, o stesso episodio) in piu' file.
            var duplicates = entries.GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1 && !group.Key.StartsWith("|", StringComparison.Ordinal))
                .OrderByDescending(group => group.Sum(entry => entry.Bytes))
                .Select(group =>
                {
                    var ordered = group.OrderByDescending(entry => entry.Declared.Width).ThenByDescending(entry => entry.Bytes).ToList();
                    return new Finding(ordered[0].Label,
                        string.Join("   +   ", ordered.Select(entry => Format(entry) + " " + Size(entry.Bytes))),
                        ordered[^1].Source.Path);
                }).ToList();

            var lowResolution = entries.Where(entry => entry.Declared.Width < 1200)
                .OrderBy(entry => entry.Declared.Width)
                .Select(entry => new Finding(entry.Label, entry.Declared.Width + "×" + entry.Declared.Height + "  ·  " + Size(entry.Bytes), entry.Source.Path)).ToList();

            // Bitrate medio dell'intero file (audio compreso): sotto queste soglie l'immagine
            // e' quasi sempre compressa in modo visibile per la sua risoluzione.
            var lowBitrate = entries
                .Where(entry => entry.Declared.Duration > 60 && entry.Declared.Width >= 1200)
                .Select(entry => (Entry: entry, Mbps: entry.Bytes * 8d / entry.Declared.Duration / 1_000_000d))
                .Where(pair => pair.Mbps < (pair.Entry.Declared.Width >= 3000 ? 12 : pair.Entry.Declared.Width >= 1800 ? 4 : 2))
                .OrderBy(pair => pair.Mbps)
                .Select(pair => new Finding(pair.Entry.Label, Format(pair.Entry) + "  ·  " + pair.Mbps.ToString("0.0", CultureInfo.CurrentCulture) + " Mbps  ·  " + Size(pair.Entry.Bytes), pair.Entry.Source.Path)).ToList();

            var hdr = new List<Finding>();
            foreach (var entry in entries.Where(entry => entry.Declared.IsHdr && entry.Declared.Transfer == "PQ"))
            {
                var d = entry.Declared;
                string? issue = null;
                if (d.DeclaredMaxCll == null)
                    issue = T("MaxCLL non dichiarato: il tone mapping deve indovinare il picco", "MaxCLL not declared: tone mapping has to guess the peak");
                else if (d.DeclaredMaxFall is int fall && fall > d.DeclaredMaxCll)
                    issue = string.Format(T("MaxFALL {0} nit superiore al MaxCLL {1} nit", "MaxFALL {0} nit above MaxCLL {1} nit"), fall, d.DeclaredMaxCll);
                else if (d.MasteringMaxNits is double mastering && d.DeclaredMaxCll > mastering * 1.05)
                    issue = string.Format(T("MaxCLL {0} nit oltre lo schermo di mastering ({1:0} nit)", "MaxCLL {0} nit beyond the mastering display ({1:0} nit)"), d.DeclaredMaxCll, mastering);
                else if (entry.Measured is { Samples.Count: > 0 } measured && measured.MeasuredPeakNits > d.DeclaredMaxCll.Value * 1.3)
                    issue = string.Format(T("MaxCLL dichiarato {0} nit, misurato {1:0} nit", "declared MaxCLL {0} nit, measured {1:0} nit"), d.DeclaredMaxCll, measured.MeasuredPeakNits);
                if (issue != null) hdr.Add(new Finding(entry.Label, issue, entry.Source.Path));
            }

            void Add(string key, List<Finding> findings) { if (findings.Count > 0) result.Sections.Add(new Section(key, findings)); }
            Add("duplicates", duplicates);
            Add("hdr", hdr);
            Add("bitrate", lowBitrate);
            Add("resolution", lowResolution);
            Add("unreadable", unreadable);
            return result;
        }
    }
}
