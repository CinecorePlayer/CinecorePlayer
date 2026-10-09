#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    internal static partial class MovieMetadataService
    {
        private sealed class TmdbApiKeyStore
        {
            private readonly string _file;
            private readonly object _lock = new();
            private string? _value;

            // Sul disco la chiave e' cifrata (SecretVault, DPAPI). "ApiKey" e' il vecchio campo in
            // chiaro: viene letto una volta e riscritto cifrato.
            private sealed class Model
            {
                public string? ApiKey { get; set; }
                public string? ProtectedApiKey { get; set; }
            }

            public TmdbApiKeyStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);
                _file = Path.Combine(folder, "tmdb.config.json");
                _value = Load();
            }

            public string? Get()
            {
                lock (_lock)
                    return _value;
            }

            public void Set(string? apiKey)
            {
                var normalized = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
                lock (_lock)
                {
                    if (string.Equals(_value, normalized, StringComparison.Ordinal))
                        return;

                    _value = normalized;
                    SaveNoLock();
                }
            }

            private string? Load()
            {
                try
                {
                    if (!File.Exists(_file))
                        return null;

                    var json = File.ReadAllText(_file, Encoding.UTF8);
                    var model = JsonSerializer.Deserialize<Model>(json);
                    string? stored = SecretVault.Unprotect(model?.ProtectedApiKey);
                    if (!string.IsNullOrWhiteSpace(stored)) return stored.Trim();
                    if (string.IsNullOrWhiteSpace(model?.ApiKey)) return null;
                    _value = model!.ApiKey!.Trim();
                    SaveNoLock();
                    return _value;
                }
                catch
                {
                    return null;
                }
            }

            private void SaveNoLock()
            {
                string? tempFile = null;
                try
                {
                    var json = JsonSerializer.Serialize(new Model { ProtectedApiKey = _value == null ? null : SecretVault.Protect(_value) }, new JsonSerializerOptions { WriteIndented = true });
                    tempFile = _file + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tempFile, json, new UTF8Encoding(false));

                    if (File.Exists(_file))
                        File.Replace(tempFile, _file, null, true);
                    else
                        File.Move(tempFile, _file);
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(tempFile))
                    {
                        try
                        {
                            if (File.Exists(tempFile))
                                File.Delete(tempFile);
                        }
                        catch { }
                    }
                }
            }
        }

        private sealed class PosterIndexStore
        {
            private sealed class PosterEntry
            {
                public string? NormalizedTitle { get; set; }
                public int? Year { get; set; }
                public string? LocalPosterPath { get; set; }
                public string? LocalBackdropPath { get; set; }
                public bool TitleResolved { get; set; }
                public string? Language { get; set; }
            }

            private sealed class Model
            {
                public Dictionary<string, PosterEntry> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public PosterIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "posterIndex.json");
                _data = Load(out var normalizedOnLoad);

                if (normalizedOnLoad)
                {
                    try { ScheduleSave(); } catch { }
                }

                try
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                    {
                        try { FlushPendingSave(); } catch { }
                    };
                }
                catch { }
            }

            /// <summary>
            /// Ritorna (titolo normalizzato, anno, path poster, stato risoluzione titolo) se esiste.
            /// </summary>
            public (string? title, int? year, string? localPosterPath, bool titleResolved, string? language)? TryGet(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (_data.Items.TryGetValue(key, out var e) || (e = FindMovedEntry(key)) != null)
                        return (e.NormalizedTitle, e.Year, e.LocalPosterPath, e.TitleResolved, e.Language);
                }

                return null;
            }

            public (string? title, int? year, string? localBackdropPath, bool titleResolved, string? language)? TryGetBackdrop(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (_data.Items.TryGetValue(key, out var e) || (e = FindMovedEntry(key)) != null)
                        return (e.NormalizedTitle, e.Year, e.LocalBackdropPath, e.TitleResolved, e.Language);
                }

                return null;
            }

            public void Reset(string path, string? title, int? year)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                bool changed = false;
                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (!_data.Items.TryGetValue(key, out var e))
                    {
                        e = new PosterEntry();
                        _data.Items[key] = e;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.Equals(e.NormalizedTitle, title, StringComparison.Ordinal))
                    {
                        e.NormalizedTitle = title;
                        changed = true;
                    }

                    if (year.HasValue && e.Year != year)
                    {
                        e.Year = year;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(e.LocalPosterPath))
                    {
                        e.LocalPosterPath = null;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(e.LocalBackdropPath))
                    {
                        e.LocalBackdropPath = null;
                        changed = true;
                    }

                    if (e.TitleResolved)
                    {
                        e.TitleResolved = false;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(e.Language))
                    {
                        e.Language = null;
                        changed = true;
                    }

                    if (changed)
                        ScheduleSave();
                }

                if (changed)
                    PostersChanged?.Invoke();
            }

            /// <summary>
            /// Aggiorna o crea l'entry relativa a quel path.
            /// </summary>
            public void Update(string path, string? title, int? year, string? localPosterPath, string? localBackdropPath = null, bool? titleResolved = null, string? language = null)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                bool changed = false;
                bool shouldNotify = false;
                string? normalizedLanguage = !string.IsNullOrWhiteSpace(language)
                    ? NormalizeTmdbLanguage(language)
                    : (titleResolved == true ? PreferredTmdbLanguage() : null);

                lock (_lock)
                {
                    string key = NormalizeIndexPathKey(path, _data.Items);
                    if (!_data.Items.TryGetValue(key, out var e))
                    {
                        e = new PosterEntry();
                        _data.Items[key] = e;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.Equals(e.NormalizedTitle, title, StringComparison.Ordinal))
                    {
                        bool hadPreviousTitle = !string.IsNullOrWhiteSpace(e.NormalizedTitle);
                        e.NormalizedTitle = title;
                        changed = true;
                        if (hadPreviousTitle || (titleResolved.HasValue && titleResolved.Value))
                            shouldNotify = true;
                    }

                    if (year.HasValue && e.Year != year)
                    {
                        e.Year = year;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(localPosterPath) &&
                        !string.Equals(e.LocalPosterPath, localPosterPath, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LocalPosterPath = localPosterPath;
                        changed = true;
                        shouldNotify = true;
                    }

                    if (!string.IsNullOrWhiteSpace(localBackdropPath) &&
                        !string.Equals(e.LocalBackdropPath, localBackdropPath, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LocalBackdropPath = localBackdropPath;
                        changed = true;
                        shouldNotify = true;
                    }

                    if (titleResolved.HasValue && e.TitleResolved != titleResolved.Value)
                    {
                        e.TitleResolved = titleResolved.Value;
                        changed = true;
                        if (titleResolved.Value)
                            shouldNotify = true;
                    }

                    if (titleResolved == true &&
                        !string.Equals(e.Language, normalizedLanguage, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Language = normalizedLanguage;
                        changed = true;
                        shouldNotify = true;
                    }
                    else if (titleResolved == false && !string.IsNullOrWhiteSpace(e.Language))
                    {
                        e.Language = null;
                        changed = true;
                    }
                }

                if (changed)
                {
                    ScheduleSave();
                    if (shouldNotify)
                        PostersChanged?.Invoke();
                }
            }

            // File spostato o rinominato di cartella (es. "M:\Film 2006 UHD...\Film 2006.mkv" ->
            // "M:\Film 2006.mkv"): riusa la voce gia' risolta con lo stesso nome file se e' l'unica
            // e il vecchio percorso non esiste piu'. Chiamare con _lock acquisito.
            private PosterEntry? FindMovedEntry(string key)
            {
                try
                {
                    if (!Path.IsPathRooted(key)) return null;
                    string fileName = Path.GetFileName(key);
                    if (string.IsNullOrWhiteSpace(fileName)) return null;
                    PosterEntry? found = null;
                    foreach (var pair in _data.Items)
                    {
                        if (!pair.Value.TitleResolved || !Path.IsPathRooted(pair.Key) ||
                            !string.Equals(Path.GetFileName(pair.Key), fileName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (found != null) return null;
                        if (File.Exists(pair.Key)) return null;
                        found = pair.Value;
                    }
                    return found;
                }
                catch { return null; }
            }

            private static string NormalizeIndexPathKey(string path, IDictionary<string, PosterEntry>? existing = null)
            {
                string key = (path ?? string.Empty).Trim();
                if (key.Length == 0)
                    return string.Empty;

                key = key.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

                if (Path.IsPathRooted(key))
                {
                    try { key = Path.GetFullPath(key); } catch { }
                    if (key.Length >= 2 && key[1] == ':')
                        key = char.ToUpperInvariant(key[0]) + key.Substring(1);

                    return key.TrimEnd(Path.DirectorySeparatorChar);
                }

                if (existing != null)
                {
                    try
                    {
                        string fileName = Path.GetFileName(key);
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            var matches = existing.Keys
                                .Where(k => !string.IsNullOrWhiteSpace(k) &&
                                            string.Equals(Path.GetFileName(k), fileName, StringComparison.OrdinalIgnoreCase))
                                .Take(2)
                                .ToList();

                            if (matches.Count == 1)
                                return matches[0];
                        }
                    }
                    catch { }
                }

                return key;
            }

            private static PosterEntry CloneEntry(PosterEntry? source)
            {
                return new PosterEntry
                {
                    NormalizedTitle = source?.NormalizedTitle,
                    Year = source?.Year,
                    LocalPosterPath = source?.LocalPosterPath,
                    LocalBackdropPath = source?.LocalBackdropPath,
                    TitleResolved = source?.TitleResolved ?? false,
                    Language = source?.Language
                };
            }

            private static void MergeEntries(PosterEntry target, PosterEntry? incoming)
            {
                if (target == null || incoming == null)
                    return;

                bool incomingHasMoreReliableTitle =
                    !string.IsNullOrWhiteSpace(incoming.NormalizedTitle) &&
                    (string.IsNullOrWhiteSpace(target.NormalizedTitle) ||
                     (!target.TitleResolved && incoming.TitleResolved));

                if (incomingHasMoreReliableTitle)
                    target.NormalizedTitle = incoming.NormalizedTitle;
                else if (string.IsNullOrWhiteSpace(target.NormalizedTitle) && !string.IsNullOrWhiteSpace(incoming.NormalizedTitle))
                    target.NormalizedTitle = incoming.NormalizedTitle;

                if (!target.Year.HasValue && incoming.Year.HasValue)
                    target.Year = incoming.Year;
                else if (incoming.Year.HasValue && incoming.TitleResolved && !target.TitleResolved)
                    target.Year = incoming.Year;

                if (string.IsNullOrWhiteSpace(target.LocalPosterPath) && !string.IsNullOrWhiteSpace(incoming.LocalPosterPath))
                    target.LocalPosterPath = incoming.LocalPosterPath;

                if (string.IsNullOrWhiteSpace(target.LocalBackdropPath) && !string.IsNullOrWhiteSpace(incoming.LocalBackdropPath))
                    target.LocalBackdropPath = incoming.LocalBackdropPath;

                if (!target.TitleResolved && incoming.TitleResolved)
                    target.TitleResolved = true;

                if (string.IsNullOrWhiteSpace(target.Language) && !string.IsNullOrWhiteSpace(incoming.Language))
                    target.Language = incoming.Language;
            }

            public string? FindEquivalentPosterPath(string path, string? title, int? year, bool allowTvEpisodePrefix = false)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
                    return null;

                string lookupTitle = NormalizeTitleForComparisonString(title);
                if (string.IsNullOrWhiteSpace(lookupTitle))
                    return null;

                lock (_lock)
                {
                    string currentKey = NormalizeIndexPathKey(path, _data.Items);

                    foreach (var kvp in _data.Items)
                    {
                        if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entry = kvp.Value;
                        // The disk check comes last: it was run for every index entry on
                        // every lookup, i.e. hundreds of stats per title while building a grid.
                        if (entry == null || string.IsNullOrWhiteSpace(entry.LocalPosterPath))
                            continue;

                        if (entry.TitleResolved && !CacheLanguageMatches(entry.Language))
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                            continue;

                        if (HasSequelOrdinalConflict(title, entry.NormalizedTitle))
                            continue;

                        string otherTitle = NormalizeTitleForComparisonString(entry.NormalizedTitle);
                        if (!EquivalentArtworkTitle(lookupTitle, otherTitle, allowTvEpisodePrefix))
                            continue;

                        if (year.HasValue && entry.Year.HasValue && Math.Abs(year.Value - entry.Year.Value) > 1)
                            continue;

                        if (!File.Exists(entry.LocalPosterPath))
                            continue;

                        return entry.LocalPosterPath;
                    }
                }

                return null;
            }

            /// <summary>
            /// Release year of the same film resolved under another path (a moved file, another
            /// disk, a network copy). The poster was already reused this way, the year was not:
            /// a file without a year in its name showed "Film" where the date belongs.
            /// </summary>
            public int? FindEquivalentYear(string path, string? title)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
                    return null;

                string lookupTitle = NormalizeTitleForComparisonString(title);
                if (string.IsNullOrWhiteSpace(lookupTitle))
                    return null;

                lock (_lock)
                {
                    string currentKey = NormalizeIndexPathKey(path, _data.Items);
                    int? unresolved = null;
                    foreach (var kvp in _data.Items)
                    {
                        if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entry = kvp.Value;
                        if (entry == null || !entry.Year.HasValue || entry.Year.Value is < 1888 or > 2100 ||
                            string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                            continue;
                        if (HasSequelOrdinalConflict(title, entry.NormalizedTitle))
                            continue;
                        if (!EquivalentArtworkTitle(lookupTitle, NormalizeTitleForComparisonString(entry.NormalizedTitle), false))
                            continue;

                        // Una voce confermata da TMDb vale piu' di un anno letto da un nome di file.
                        if (entry.TitleResolved) return entry.Year;
                        unresolved ??= entry.Year;
                    }
                    return unresolved;
                }
            }

            public string? FindEquivalentBackdropPath(string path, string? title, int? year, bool allowTvEpisodePrefix = false)
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
                    return null;

                string lookupTitle = NormalizeTitleForComparisonString(title);
                if (string.IsNullOrWhiteSpace(lookupTitle))
                    return null;

                lock (_lock)
                {
                    string currentKey = NormalizeIndexPathKey(path, _data.Items);

                    foreach (var kvp in _data.Items)
                    {
                        if (string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var entry = kvp.Value;
                        // The disk check comes last: it was run for every index entry on
                        // every lookup, i.e. hundreds of stats per title while building a grid.
                        if (entry == null || string.IsNullOrWhiteSpace(entry.LocalBackdropPath))
                            continue;

                        if (entry.TitleResolved && !CacheLanguageMatches(entry.Language))
                            continue;

                        if (string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                            continue;

                        if (HasSequelOrdinalConflict(title, entry.NormalizedTitle))
                            continue;

                        string otherTitle = NormalizeTitleForComparisonString(entry.NormalizedTitle);
                        if (!EquivalentArtworkTitle(lookupTitle, otherTitle, allowTvEpisodePrefix))
                            continue;

                        if (year.HasValue && entry.Year.HasValue && Math.Abs(year.Value - entry.Year.Value) > 1)
                            continue;

                        if (!File.Exists(entry.LocalBackdropPath))
                            continue;

                        return entry.LocalBackdropPath;
                    }
                }

                return null;
            }

            private static bool EquivalentArtworkTitle(string lookupTitle, string otherTitle, bool allowTvEpisodePrefix)
            {
                if (string.Equals(lookupTitle, otherTitle, StringComparison.Ordinal))
                    return true;
                if (!allowTvEpisodePrefix || !otherTitle.StartsWith(lookupTitle, StringComparison.Ordinal))
                    return false;

                string suffix = otherTitle[lookupTitle.Length..].Trim();
                return Regex.IsMatch(suffix, @"^s\d{1,3}e\d{1,4}\b", RegexOptions.IgnoreCase) ||
                       Regex.IsMatch(suffix, @"^(season|stagione)\s*\d+\b", RegexOptions.IgnoreCase);
            }

            private Model Load(out bool normalizedOnLoad)
            {
                normalizedOnLoad = false;

                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m?.Items != null)
                        {
                            var rebuilt = new Dictionary<string, PosterEntry>(StringComparer.OrdinalIgnoreCase);

                            foreach (var kvp in m.Items)
                            {
                                string key = NormalizeIndexPathKey(kvp.Key ?? string.Empty, rebuilt);
                                if (string.IsNullOrWhiteSpace(key))
                                    continue;

                                if (!rebuilt.TryGetValue(key, out var entry))
                                {
                                    rebuilt[key] = CloneEntry(kvp.Value);
                                }
                                else
                                {
                                    MergeEntries(entry, kvp.Value);
                                    normalizedOnLoad = true;
                                }

                                if (!string.Equals(key, kvp.Key?.Trim(), StringComparison.Ordinal))
                                    normalizedOnLoad = true;
                            }

                            m.Items = rebuilt;
                            return m;
                        }
                    }
                }
                catch
                {
                    // se fallisce, partiamo da pulito
                }

                return new Model();
            }

            private CancellationTokenSource? _saveCts;

            private void FlushPendingSave()
            {
                string json;
                CancellationTokenSource? toDispose = null;
                lock (_lock)
                {
                    toDispose = _saveCts;
                    _saveCts = null;
                    json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                }

                try { toDispose?.Cancel(); } catch { }
                try { toDispose?.Dispose(); } catch { }

                string tmp = _file + ".tmp";
                try
                {
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));

                    if (File.Exists(_file))
                    {
                        try
                        {
                            File.Replace(tmp, _file, null, true);
                        }
                        catch
                        {
                            File.Copy(tmp, _file, true);
                            File.Delete(tmp);
                        }
                    }
                    else
                    {
                        File.Move(tmp, _file);
                    }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tmp))
                            File.Delete(tmp);
                    }
                    catch { }
                }
            }

            private void ScheduleSave()
            {
                FlushPendingSave();
            }
        }
    }
}
