#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    // Genius: copre molti brani che i database di testi sincronizzati non hanno (inediti,
    // demo, live). Il testo arriva senza tempi: lo allinea poi la sincronizzazione in
    // background, come per gli altri testi semplici.
    public sealed partial class MusicLyricsService
    {
        private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";
        private static readonly Regex GeniusContainerOpen = new(@"<div[^>]*data-lyrics-container=""true""[^>]*>", RegexOptions.Compiled);
        private static readonly Regex HtmlBreak = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex SectionHeader = new(@"^\s*\[[^\]]{1,60}\]\s*$", RegexOptions.Compiled);

        // Etichette dei file inediti che su Genius non fanno parte del titolo.
        private static readonly Regex UnreleasedTag = new(@"\s*[\(\[](unreleased|snippet|demo|leak(ed)?|alternative version|alt\.? version|live|acoustic|full version|hq|cdq)[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private async Task<LyricsResult?> TryGeniusAsync(LyricsQuery query, CancellationToken ct)
        {
            string cleanTitle = UnreleasedTag.Replace(query.Title, "").Trim();
            if (cleanTitle.Length > 0 && cleanTitle != query.Title) query = query with { Title = cleanTitle };
            string term = string.IsNullOrWhiteSpace(query.Artist) ? query.Title : query.Artist + " " + query.Title;
            using var search = new HttpRequestMessage(HttpMethod.Get, "https://genius.com/api/search/song?per_page=10&q=" + Uri.EscapeDataString(term));
            search.Headers.UserAgent.Clear();
            search.Headers.UserAgent.ParseAdd(BrowserAgent);
            using var searchRes = await _http.SendAsync(search, ct).ConfigureAwait(false);
            if (!searchRes.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await searchRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            var hits = new List<(string Title, string Artist, string Url)>();
            if (doc.RootElement.TryGetProperty("response", out var response) && response.TryGetProperty("sections", out var sections))
                foreach (var section in sections.EnumerateArray())
                    if (section.TryGetProperty("hits", out var list))
                        foreach (var hit in list.EnumerateArray())
                        {
                            if (!hit.TryGetProperty("result", out var r)) continue;
                            string title = ReadString(r, "title") ?? "";
                            string url = ReadString(r, "url") ?? "";
                            string artist = r.TryGetProperty("primary_artist", out var pa) ? ReadString(pa, "name") ?? "" : ReadString(r, "artist_names") ?? "";
                            if (title.Length > 0 && url.StartsWith("https://genius.com/", StringComparison.Ordinal)) hits.Add((title, artist, url));
                        }

            // Niente traduzioni o pagine editoriali di Genius ("Genius Traductions", "Genius English Translations").
            var best = hits
                .Where(h => !h.Artist.StartsWith("Genius", StringComparison.OrdinalIgnoreCase) && !h.Url.Contains("-translation", StringComparison.OrdinalIgnoreCase) && !h.Url.Contains("traduction", StringComparison.OrdinalIgnoreCase))
                .Select(h => (Hit: h, Score: (StrongTitleMatch(h.Title, query.Title) || StrongTitleMatch(BaseTitle(h.Title), BaseTitle(query.Title)) ? 4 : Similar(h.Title, query.Title) ? 2 : 0)
                    + (string.IsNullOrWhiteSpace(query.Artist) ? 1 : Similar(h.Artist, query.Artist) ? 3 : 0)))
                .Where(x => x.Score >= 5 || (string.IsNullOrWhiteSpace(query.Artist) && x.Score >= 5 - 2))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Hit)
                .FirstOrDefault();
            if (best.Url == null) return null;

            using var page = new HttpRequestMessage(HttpMethod.Get, best.Url);
            page.Headers.UserAgent.Clear();
            page.Headers.UserAgent.ParseAdd(BrowserAgent);
            page.Headers.Accept.Clear();
            page.Headers.Accept.ParseAdd("text/html");
            using var pageRes = await _http.SendAsync(page, ct).ConfigureAwait(false);
            if (!pageRes.IsSuccessStatusCode) return null;
            string text = ExtractGeniusLyrics(await pageRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!LooksLikeLyrics(text)) return null;
            return new LyricsResult("Genius", text, false, $"{best.Title} - {best.Artist}", Array.Empty<LyricsLine>());
        }

        // Titolo senza parentesi finali: "Murder Song (5, 4, 3, 2, 1)" e "Murder song" sono lo stesso brano.
        private static string BaseTitle(string title) => Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]\s*", " ").Trim();

        /// <summary>Testo dai contenitori "data-lyrics-container" della pagina del brano.</summary>
        internal static string ExtractGeniusLyrics(string html)
        {
            var parts = new List<string>();
            foreach (Match open in GeniusContainerOpen.Matches(html))
            {
                // I contenitori hanno div annidati: si segue la profondita' fino alla chiusura.
                int i = open.Index + open.Length, j = i, depth = 1;
                while (depth > 0 && j < html.Length)
                {
                    int nextOpen = html.IndexOf("<div", j, StringComparison.Ordinal);
                    int nextClose = html.IndexOf("</div>", j, StringComparison.Ordinal);
                    if (nextClose < 0) { j = html.Length; break; }
                    if (nextOpen >= 0 && nextOpen < nextClose) { depth++; j = nextOpen + 4; }
                    else { depth--; j = nextClose + 6; }
                }
                string block = html.Substring(i, Math.Max(0, Math.Min(html.Length, j - 6) - i));
                // Via le parti escluse dalla selezione (intestazione "N Contributors", descrizione).
                block = RemoveExcludedDivs(block);
                parts.Add(block);
            }
            string raw = HtmlBreak.Replace(string.Join("\n", parts), "\n");
            raw = WebUtility.HtmlDecode(HtmlTag.Replace(raw, ""));

            var lines = new List<string>();
            foreach (string line in raw.Replace("\r", "").Split('\n'))
            {
                string t = line.Trim();
                if (SectionHeader.IsMatch(t)) { if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(""); continue; }
                if (t.Contains("Read More", StringComparison.Ordinal) || t.StartsWith("You might also like", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
                lines.Add(t);
            }
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\n", lines);
        }

        private static string RemoveExcludedDivs(string block)
        {
            const string marker = "data-exclude-from-selection=\"true\"";
            int at;
            while ((at = block.IndexOf(marker, StringComparison.Ordinal)) >= 0)
            {
                int start = block.LastIndexOf("<div", at, StringComparison.Ordinal);
                if (start < 0) break;
                int j = block.IndexOf('>', at) + 1, depth = 1;
                while (depth > 0 && j > 0 && j < block.Length)
                {
                    int o = block.IndexOf("<div", j, StringComparison.Ordinal), c = block.IndexOf("</div>", j, StringComparison.Ordinal);
                    if (c < 0) { j = block.Length; break; }
                    if (o >= 0 && o < c) { depth++; j = o + 4; } else { depth--; j = c + 6; }
                }
                block = block.Remove(start, Math.Max(0, Math.Min(block.Length, j) - start));
            }
            return block;
        }
    }
}
