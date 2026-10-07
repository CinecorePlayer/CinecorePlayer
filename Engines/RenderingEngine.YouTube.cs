#nullable enable
using CinecorePlayer2025.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CinecorePlayer2025.Engines
{
    public sealed partial class DirectShowUnifiedEngine
    {
        private sealed class YouTubeResolveResult
        {
            public string VideoUrl = string.Empty;
            public string? AudioUrl;
            public string? Title;
            public string? VideoId;
            public int? SelectedHeight;
            public List<int>? AvailableHeights;
        }

        private sealed class YtCacheEntry
        {
            public DateTime CreatedUtc;
            public string Json = string.Empty;
        }

        private static readonly object _ytCacheLock = new();
        private static readonly Dictionary<string, YtCacheEntry> _ytCache = new(StringComparer.Ordinal);
        private static readonly TimeSpan _ytCacheTtl = TimeSpan.FromMinutes(20);

        private static bool IsYouTubeUrl(string pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
                return false;

            if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u))
                return false;

            if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;

            var host = (u.Host ?? string.Empty).ToLowerInvariant();
            return host.EndsWith("youtube.com") || host.EndsWith("youtu.be") || host.EndsWith("youtube-nocookie.com");
        }

        private static string NormalizeYouTubeUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                    return url;

                var id = TryGetYouTubeVideoId(u);
                if (!string.IsNullOrWhiteSpace(id))
                    return "https://www.youtube.com/watch?v=" + id;

                return url;
            }
            catch
            {
                return url;
            }
        }

        private static string? TryGetYouTubeVideoId(Uri u)
        {
            try
            {
                var host = (u.Host ?? string.Empty).ToLowerInvariant();
                var path = (u.AbsolutePath ?? string.Empty).Trim('/');

                // youtu.be/<id>
                if (host.EndsWith("youtu.be"))
                {
                    var seg = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length > 0) return seg[0];
                }

                // youtube.com/watch?v=<id>
                if (path.StartsWith("watch", StringComparison.OrdinalIgnoreCase))
                {
                    var v = GetQueryParam(u.Query, "v");
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }

                // youtube.com/embed/<id>
                if (path.StartsWith("embed/", StringComparison.OrdinalIgnoreCase))
                {
                    var id = path.Substring("embed/".Length);
                    if (!string.IsNullOrWhiteSpace(id)) return id.Split('/')[0];
                }

                // youtube.com/shorts/<id>
                if (path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase))
                {
                    var id = path.Substring("shorts/".Length);
                    if (!string.IsNullOrWhiteSpace(id)) return id.Split('/')[0];
                }

                // fallback: prova comunque la query v=
                var v2 = GetQueryParam(u.Query, "v");
                if (!string.IsNullOrWhiteSpace(v2)) return v2;
            }
            catch { }

            return null;
        }

        private static string? GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key))
                return null;

            var q = query;
            if (q.StartsWith("?", StringComparison.Ordinal))
                q = q.Substring(1);

            foreach (var part in q.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;

                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 0) continue;

                var k = Uri.UnescapeDataString(kv[0] ?? string.Empty);
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kv.Length < 2) return string.Empty;
                return Uri.UnescapeDataString(kv[1] ?? string.Empty);
            }

            return null;
        }

        private static bool LooksLikeVideoUrl(string pathOrUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrUrl))
                    return false;

                if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u))
                    return false;

                if (!string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                    return false;

                var lower = pathOrUrl.ToLowerInvariant();

                // audio-only hints
                if (lower.Contains("mime=audio"))
                    return false;

                if (lower.EndsWith(".mp3") || lower.EndsWith(".m4a") || lower.EndsWith(".aac")
                    || lower.EndsWith(".flac") || lower.EndsWith(".wav") || lower.EndsWith(".ogg")
                    || lower.EndsWith(".opus"))
                    return false;

                // video hints
                if (lower.Contains("mime=video"))
                    return true;

                if (lower.Contains("googlevideo.com/videoplayback"))
                    return true;

                if (lower.EndsWith(".mp4") || lower.EndsWith(".mkv") || lower.EndsWith(".webm") || lower.EndsWith(".mov"))
                    return true;

                if (lower.EndsWith(".m3u8") || lower.EndsWith(".mpd"))
                    return true;

                // fallback: URL http(s) generico → probabile video
                return true;
            }
            catch { return false; }
        }

        private static bool TryResolveYouTubeStreams(string youtubeUrl, int? preferredHeight, out YouTubeResolveResult result, out string error)
        {
            result = new YouTubeResolveResult();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(youtubeUrl))
            {
                error = "URL vuoto.";
                return false;
            }

            string? exe = FindYtDlpExecutable();
            if (string.IsNullOrWhiteSpace(exe))
            {
                error = global::CinecorePlayer2025.Utilities.AppLanguage.T("yt-dlp non trovato. Sono supportati yt-dlp.cmd, yt-dlp.bat, yt-dlp.exe nella cartella third-parties\\yt-dlp, nella cartella dell'app o nel PATH.", "yt-dlp not found. yt-dlp.cmd, yt-dlp.bat or yt-dlp.exe are supported in third-parties\\yt-dlp, in the app folder or on PATH.");
                return false;
            }

            // key cache: preferiamo l'ID video se disponibile
            string cacheKey = youtubeUrl;
            try
            {
                if (Uri.TryCreate(youtubeUrl, UriKind.Absolute, out var u))
                {
                    var id = TryGetYouTubeVideoId(u);
                    if (!string.IsNullOrWhiteSpace(id))
                        cacheKey = id!;
                }
            }
            catch { }

            if (!TryGetYouTubeJson(exe, youtubeUrl, cacheKey, out var json, out var jsonErr))
            {
                error = jsonErr;
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                // yt-dlp può restituire un playlist wrapper (anche con --no-playlist in certi casi).
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.Object)
                        {
                            root = e;
                            break;
                        }
                    }
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("JSON yt-dlp non valido.", "Invalid yt-dlp JSON.");
                    return false;
                }

                result.VideoId = TryGetString(root, "id");
                result.Title = TryGetString(root, "title");

                if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Nessun formato disponibile (formats mancante).", "No formats available (formats missing).");
                    return false;
                }

                // raccogli video candidates
                var videoCands = new List<YtFmt>();
                var audioCands = new List<YtFmt>();
                var heights = new HashSet<int>();

                foreach (var f in formats.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;

                    var url = TryGetString(f, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;

                    var vcodec = TryGetString(f, "vcodec") ?? string.Empty;
                    var acodec = TryGetString(f, "acodec") ?? string.Empty;

                    var ext = TryGetString(f, "ext") ?? string.Empty;
                    int height = TryGetInt(f, "height") ?? 0;

                    double tbr = TryGetDouble(f, "tbr") ?? 0.0;
                    double abr = TryGetDouble(f, "abr") ?? 0.0;

                    var fmt = new YtFmt
                    {
                        Url = url!,
                        VCodec = vcodec,
                        ACodec = acodec,
                        Ext = ext,
                        Height = height,
                        Tbr = tbr,
                        Abr = abr
                    };

                    bool hasV = !string.Equals(vcodec, "none", StringComparison.OrdinalIgnoreCase);
                    bool hasA = !string.Equals(acodec, "none", StringComparison.OrdinalIgnoreCase);

                    if (hasV)
                    {
                        videoCands.Add(fmt);
                        if (height > 0) heights.Add(height);
                    }
                    if (!hasV && hasA)
                    {
                        audioCands.Add(fmt);
                    }
                }

                if (videoCands.Count == 0)
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Nessun formato video trovato.", "No video format found.");
                    return false;
                }

                // altezze disponibili (ordinate)
                var heightsList = heights.OrderBy(h => h).ToList();
                result.AvailableHeights = heightsList;

                // sceglie altezza
                int chosenHeight = 0;
                if (heightsList.Count > 0)
                {
                    if (preferredHeight.HasValue && preferredHeight.Value > 0)
                    {
                        int target = preferredHeight.Value;

                        var le = heightsList.Where(h => h <= target).OrderByDescending(h => h).FirstOrDefault();
                        if (le > 0)
                            chosenHeight = le;
                        else
                            chosenHeight = heightsList.OrderBy(h => h).First(); // nessuna <= target: usa la più bassa disponibile
                    }
                    else
                    {
                        chosenHeight = heightsList.Max();
                    }
                }

                // scegli best video (a parità di height)
                YtFmt bestVideo = default;
                bool bestVideoSet = false;
                int bestScore = int.MinValue;

                foreach (var v in videoCands)
                {
                    // se abbiamo un chosenHeight, filtra per quello (quando possibile)
                    if (chosenHeight > 0 && v.Height > 0 && v.Height != chosenHeight)
                        continue;

                    int score = 0;

                    // preferisci container/codec più compatibili
                    if (string.Equals(v.Ext, "mp4", StringComparison.OrdinalIgnoreCase)) score += 100;
                    if (string.Equals(v.Ext, "mkv", StringComparison.OrdinalIgnoreCase)) score += 10;
                    if (string.Equals(v.Ext, "webm", StringComparison.OrdinalIgnoreCase)) score += 5;

                    if (v.VCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || v.VCodec.Contains("avc1", StringComparison.OrdinalIgnoreCase)) score += 60;
                    if (v.VCodec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) || v.VCodec.Contains("hev1", StringComparison.OrdinalIgnoreCase) || v.VCodec.Contains("h265", StringComparison.OrdinalIgnoreCase)) score += 50;
                    if (v.VCodec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) || v.VCodec.Contains("vp9", StringComparison.OrdinalIgnoreCase)) score += 20;
                    if (v.VCodec.StartsWith("av01", StringComparison.OrdinalIgnoreCase) || v.VCodec.Contains("av1", StringComparison.OrdinalIgnoreCase)) score += 10;

                    // bitrate
                    score += (int)Math.Round(v.Tbr);

                    // piccolo bonus se è muxed (audio incluso): evita stream separati quando possibile
                    if (!string.Equals(v.ACodec, "none", StringComparison.OrdinalIgnoreCase))
                        score += 15;

                    // altezza: in fallback (quando chosenHeight=0), preferisci più grande
                    score += v.Height;

                    if (!bestVideoSet || score > bestScore)
                    {
                        bestVideoSet = true;
                        bestScore = score;
                        bestVideo = v;
                    }
                }

                if (!bestVideoSet)
                {
                    // fallback: prendi la migliore in assoluto (senza height)
                    foreach (var v in videoCands)
                    {
                        int score = (int)Math.Round(v.Tbr) + v.Height;
                        if (string.Equals(v.Ext, "mp4", StringComparison.OrdinalIgnoreCase)) score += 50;
                        if (!bestVideoSet || score > bestScore)
                        {
                            bestVideoSet = true;
                            bestScore = score;
                            bestVideo = v;
                        }
                    }
                }

                if (!bestVideoSet || string.IsNullOrWhiteSpace(bestVideo.Url))
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Impossibile selezionare un formato video.", "Could not select a video format.");
                    return false;
                }

                result.VideoUrl = bestVideo.Url;
                result.SelectedHeight = (bestVideo.Height > 0 ? bestVideo.Height : chosenHeight > 0 ? chosenHeight : (int?)null);

                // audio: se il video scelto non include audio, scegli best audio-only
                if (string.Equals(bestVideo.ACodec, "none", StringComparison.OrdinalIgnoreCase))
                {
                    if (audioCands.Count == 0)
                    {
                        // alcuni formati "best" includono audio già nel video; se qui non c'è audio, accettiamo silent
                        // ma segnaliamo come warn
                        error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Formato video selezionato senza audio e nessun formato audio trovato.", "The selected video format has no audio and no audio format was found.");
                        // non fail hard: riproduzione video-only possibile
                        result.AudioUrl = null;
                        return true;
                    }

                    YtFmt bestAudio = default;
                    bool bestAudioSet = false;
                    int bestAScore = int.MinValue;

                    foreach (var a in audioCands)
                    {
                        int score = 0;
                        if (string.Equals(a.Ext, "m4a", StringComparison.OrdinalIgnoreCase) || string.Equals(a.Ext, "mp4", StringComparison.OrdinalIgnoreCase)) score += 100;
                        if (a.ACodec.Contains("mp4a", StringComparison.OrdinalIgnoreCase)) score += 50;
                        if (a.ACodec.Contains("aac", StringComparison.OrdinalIgnoreCase)) score += 40;
                        if (a.ACodec.Contains("opus", StringComparison.OrdinalIgnoreCase)) score += 10;

                        score += (int)Math.Round(a.Abr > 0 ? a.Abr : a.Tbr);

                        if (!bestAudioSet || score > bestAScore)
                        {
                            bestAudioSet = true;
                            bestAScore = score;
                            bestAudio = a;
                        }
                    }

                    if (bestAudioSet && !string.IsNullOrWhiteSpace(bestAudio.Url))
                        result.AudioUrl = bestAudio.Url;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Errore parse yt-dlp JSON: ", "yt-dlp JSON parse error: ") + ex.Message;
                return false;
            }
        }

        private struct YtFmt
        {
            public string Url;
            public string VCodec;
            public string ACodec;
            public string Ext;
            public int Height;
            public double Tbr;
            public double Abr;
        }

        private static string? TryGetString(JsonElement obj, string prop)
        {
            try
            {
                if (obj.ValueKind != JsonValueKind.Object) return null;
                if (!obj.TryGetProperty(prop, out var v)) return null;
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                return null;
            }
            catch { return null; }
        }

        private static int? TryGetInt(JsonElement obj, string prop)
        {
            try
            {
                if (obj.ValueKind != JsonValueKind.Object) return null;
                if (!obj.TryGetProperty(prop, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                return null;
            }
            catch { return null; }
        }

        private static double? TryGetDouble(JsonElement obj, string prop)
        {
            try
            {
                if (obj.ValueKind != JsonValueKind.Object) return null;
                if (!obj.TryGetProperty(prop, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                return null;
            }
            catch { return null; }
        }

        private static string? FindYtDlpExecutable()
        {
            return YtDlpLocator.Find();
        }

        private static bool TryGetYouTubeJson(string ytDlpExe, string youtubeUrl, string cacheKey, out string json, out string error)
        {
            json = string.Empty;
            error = string.Empty;

            // cache
            try
            {
                lock (_ytCacheLock)
                {
                    if (_ytCache.TryGetValue(cacheKey, out var e))
                    {
                        if (DateTime.UtcNow - e.CreatedUtc < _ytCacheTtl && !string.IsNullOrWhiteSpace(e.Json))
                        {
                            json = e.Json;
                            return true;
                        }

                        _ytCache.Remove(cacheKey);
                    }
                }
            }
            catch { }

            string stdout = string.Empty;
            string stderr = string.Empty;

            if (!YtDlpLocator.IsSafeArgumentValue(youtubeUrl))
            {
                error = "URL non valido.";
                return false;
            }

            try
            {
                var psi = YtDlpLocator.CreateProcessStartInfo(
                    ytDlpExe,
                    "-J --no-playlist --no-warnings -- " + QuoteArg(youtubeUrl));

                using var p = new Process { StartInfo = psi };
                if (!p.Start())
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Impossibile avviare yt-dlp.", "Could not start yt-dlp.");
                    return false;
                }

                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();

                if (!p.WaitForExit(20000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    error = "Timeout yt-dlp.";
                    return false;
                }

                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    error = global::CinecorePlayer2025.Utilities.AppLanguage.T("yt-dlp ha restituito errore (exit=", "yt-dlp returned an error (exit=") + p.ExitCode + "). " + (string.IsNullOrWhiteSpace(stderr) ? "" : stderr.Trim());
                    return false;
                }

                json = stdout;

                // salva in cache
                try
                {
                    lock (_ytCacheLock)
                    {
                        _ytCache[cacheKey] = new YtCacheEntry { CreatedUtc = DateTime.UtcNow, Json = json };
                    }
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                error = global::CinecorePlayer2025.Utilities.AppLanguage.T("Errore yt-dlp: ", "yt-dlp error: ") + ex.Message + (string.IsNullOrWhiteSpace(stderr) ? "" : " | " + stderr.Trim());
                return false;
            }
        }

        private static string QuoteArg(string s)
        {
            return "\"" + (s ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        // -------------------------------------------------------------------

    }
}
