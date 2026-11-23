using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CinecoreMac;

/// <summary>
/// Lingue dell'interfaccia (italiano, inglese, spagnolo; Impostazioni › Generali › Lingua, come su Windows).
/// L'app e' scritta in italiano: i testi si traducono quando si disegnano (ogni FormattedText passa da qui), quindi la
/// logica che guarda i testi resta in italiano. Le traduzioni stanno in Lang/strings.tsv (italiano, inglese, spagnolo,
/// separati da tabulazione; {0} {1} = numeri, {s} = una parte qualsiasi, es. un titolo). Per lo spagnolo, se manca,
/// vale la traduzione di Windows cercata con il testo inglese (SpanishTexts).
/// </summary>
internal static class L10n
{
    public static string Language { get; private set; } = Normalize(MacSettings.Get("language", ""));
    public static event Action? Changed;

    public static string Normalize(string? code) => code is "it" or "en" or "es" ? code : SystemLanguage();

    /// <summary>Prima lingua preferita di macOS ([NSLocale preferredLanguages]); l'app gira senza culture .NET.</summary>
    private static string SystemLanguage()
    {
#if CINECORE_WINDOWS
        // Lingua dell'interfaccia di Windows (LANGID: 0x09 inglese, 0x0A spagnolo, il resto italiano).
        try
        {
            int primary = GetUserDefaultUILanguage() & 0x3FF;
            return primary == 0x09 ? "en" : primary == 0x0A ? "es" : "it";
        }
        catch { return "it"; }
#endif
        try
        {
            IntPtr list = Send(objc_getClass("NSLocale"), sel_registerName("preferredLanguages"));
            if (list != IntPtr.Zero && Send(list, sel_registerName("firstObject")) is var first && first != IntPtr.Zero)
            {
                string code = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Send(first, sel_registerName("UTF8String"))) ?? "";
                if (code.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es";
                if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            }
        }
        catch { }
        return "it";
    }

#if CINECORE_WINDOWS
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern ushort GetUserDefaultUILanguage();
#endif
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getClass(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel);

    public static readonly (string Code, string Name)[] Languages = { ("it", "Italiano"), ("en", "English"), ("es", "Español") };

    public static void Set(string code)
    {
        code = Normalize(code);
        MacSettings.Set("language", code);
        if (code == Language) return;
        Language = code;
        Cache.Clear();
        try { CinecorePlayer2025.Utilities.AppLanguage.Current = code; } catch { }
        Changed?.Invoke();
    }

    private sealed record Entry(string En, string Es);
    private static Dictionary<string, Entry>? _exact;
    private static List<(Regex Pattern, string It, Entry Entry)>? _templates;
    private static List<(string Prefix, Entry Entry)>? _prefixes;
    private static readonly Dictionary<string, Entry> _upper = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Regex Numbers = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    // Separatori dei testi composti (tenuti nello Split dal gruppo): il punto in mezzo con i suoi spazi, o 3+ spazi.
    private static readonly Regex Separators = new(@"(\s*[·•]\s*|\s{3,})", RegexOptions.Compiled);

    private static void Load()
    {
        if (_exact != null) return;
        var exact = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var templates = new List<(Regex, string, Entry)>();
        var prefixes = new List<(string, Entry)>();
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(AppAssets.Root + "Lang/strings.tsv"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                string it = Decode(parts[0]), en = Decode(parts[1]), es = parts.Length > 2 ? Decode(parts[2]) : "";
                var entry = new Entry(en, es);
                exact[it] = entry;
                if (it.Contains("{s}") || it.Contains("{0}"))
                {
                    // {0} = numero, {s} = testo qualsiasi; il resto alla lettera.
                    var sb = new StringBuilder("^");
                    foreach (var piece in Regex.Split(it, @"(\{s\}|\{\d\})"))
                        sb.Append(piece == "{s}" ? "(.+?)" : Regex.IsMatch(piece, @"^\{\d\}$") ? @"(\d+(?:[.,]\d+)?)" : Regex.Escape(piece));
                    sb.Append('$');
                    templates.Add((new Regex(sb.ToString(), RegexOptions.Singleline), it, entry));
                }
                else if (it.Length >= 3 && (it.EndsWith(' ') || it.EndsWith('“') || it.EndsWith(": ")))
                    prefixes.Add((it, entry));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[lingua] traduzioni non caricate: " + ex.Message); }
        prefixes.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        foreach (var (it, entry) in exact.ToList())
            if (it.Any(char.IsLower)) _upper.TryAdd(it.ToUpperInvariant(), entry);
        _templates = templates; _prefixes = prefixes; _exact = exact;
    }

    private static string Decode(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

    private static string? Pick(Entry e)
    {
        if (Language == "en") return e.En.Length > 0 ? e.En : null;
        if (e.Es.Length > 0) return e.Es;
        if (e.En.Length == 0) return null;
        try { return CinecorePlayer2025.Utilities.SpanishTexts.Get(e.En); } catch { return e.En; }
    }

    private static readonly bool QaMissing = Environment.GetEnvironmentVariable("CINECORE_QA_L10N") == "1";

    /// <summary>Il testo nella lingua scelta (l'originale se manca la traduzione).</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
#if CINECORE_WINDOWS
        if (Language == "it") return WindowsWords(text)!;
#else
        if (Language == "it") return text;
#endif
        if (Cache.TryGetValue(text, out var done)) return done;
        string result = Translate(text);
#if CINECORE_WINDOWS
        // [2026-11-22] Le traduzioni sono scritte sul testo del Mac ("sul Mac", "Finder"): prima si traduce, poi si
        // cambiano le parole nella lingua di arrivo. Prima si cambiavano in italiano e la traduzione non si trovava piu'
        // (su Windows in inglese restavano in italiano "Mostra in Esplora file", "Scarica sottotitoli… (solo file sul PC)").
        if (!ReferenceEquals(result, text)) result = ForeignWindowsWords(result);
        else if (WindowsWords(text) is { } windows && !ReferenceEquals(windows, text)) result = Translate(windows);
#endif
        // Prova (CINECORE_QA_L10N=1): i testi con lettere rimasti in italiano, una volta ciascuno.
        if (QaMissing && ReferenceEquals(result, text) && text.Any(char.IsLetter)) Console.Error.WriteLine("[l10n-manca] " + text.Replace('\n', '|'));
        if (Cache.Count > 20000) Cache.Clear();
        Cache[text] = result;
        return result;
    }

#if CINECORE_WINDOWS
    // I testi dell'interfaccia nascono per il Mac: su Windows si dice PC, Esplora file, Avvio di Windows.
    private static readonly (string Mac, string Windows)[] WindowsTerms =
    {
        ("Apri Cinecore all’avvio del Mac", "Avvia Cinecore con Windows"),
        ("Come in Impostazioni di Sistema › Elementi login.", "Come in Impostazioni › App › Avvio."),
        ("Cinecore Player per Mac", "Cinecore Player per Windows"),
        ("prossime versioni per Mac", "prossime versioni per Windows"),
        ("Mostra nel Finder", "Mostra in Esplora file"),
        ("nel Finder", "in Esplora file"),
        ("dal Finder", "da Esplora file"),
        ("Macintosh HD", "Disco locale"),
        ("schermo del Mac", "schermo del PC"),
        ("sul Mac", "sul PC"),
        ("del Mac", "del PC"),
        ("al Mac", "al PC"),
        ("per Mac", "per Windows"),
        ("il Mac", "il PC"),
        ("(⇧⌘Z)", "(Ctrl+Maiusc+Z)"),
        ("(⌘Z)", "(Ctrl+Z)"),
        ("(⌘S)", "(Ctrl+S)"),
        ("(⇧R)", "(Maiusc+R)"),
        ("In attesa di macOS…", "In attesa di Windows…"),
        ("Come macOS", "Come Windows"),
        ("salvata nel Portachiavi", "salvata cifrata su questo PC"),
        ("nel Portachiavi", "cifrate su questo PC"),
        ("macOS", "Windows"),
    };
    private static readonly (string Mac, string Windows)[] EnglishWindowsTerms =
    {
        ("Open Cinecore when the Mac starts", "Start Cinecore with Windows"),
        ("Show in Finder", "Show in File Explorer"), ("in Finder", "in File Explorer"), ("from Finder", "from File Explorer"),
        ("Mac update channel", "Windows update channel"), ("future Mac version", "future Windows version"),
        ("for Mac", "for Windows"), ("Mac name", "PC name"), ("Mac library", "PC library"),
        ("the Mac's", "the PC's"), ("the Mac", "the PC"), ("Waiting for macOS", "Waiting for Windows"),
        ("(⇧⌘Z)", "(Ctrl+Shift+Z)"), ("(⌘Z)", "(Ctrl+Z)"), ("(⌘S)", "(Ctrl+S)"), ("(⇧R)", "(Shift+R)"), ("macOS", "Windows"),
    };
    private static readonly (string Mac, string Windows)[] SpanishWindowsTerms =
    {
        ("Abrir Cinecore al iniciar el Mac", "Iniciar Cinecore con Windows"),
        ("en el Finder", "en el Explorador de archivos"), ("el Finder", "el Explorador de archivos"),
        ("para Mac", "para Windows"), ("del Mac", "del PC"), ("el Mac", "el PC"), ("al Mac", "al PC"),
        ("(⇧⌘Z)", "(Ctrl+Mayús+Z)"), ("(⌘Z)", "(Ctrl+Z)"), ("(⌘S)", "(Ctrl+S)"), ("(⇧R)", "(Mayús+R)"), ("macOS", "Windows"),
    };

    private static string ForeignWindowsWords(string text)
    {
        if (!Array.Exists(WindowsTriggers, w => text.Contains(w, StringComparison.Ordinal)) && !text.Contains("macOS", StringComparison.Ordinal)) return text;
        if (text == "Mac") return "PC";
        foreach (var (mac, win) in Language == "es" ? SpanishWindowsTerms : EnglishWindowsTerms) text = text.Replace(mac, win, StringComparison.Ordinal);
        return text;
    }

    private static readonly string[] WindowsTriggers = { "Mac", "Finder", "Elementi login", "⌘", "⇧", "Portachiavi" };
    private static readonly ConcurrentDictionary<string, string> WindowsCache = new(StringComparer.Ordinal);

    private static string? WindowsWords(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Array.Exists(WindowsTriggers, w => text.Contains(w, StringComparison.Ordinal)) && !text.Contains("macOS", StringComparison.Ordinal)) return text;
        return WindowsCache.GetOrAdd(text, t =>
        {
            if (t == "Mac") return "PC";
            foreach (var (mac, win) in WindowsTerms) t = t.Replace(mac, win, StringComparison.Ordinal);
            return t;
        });
    }
#endif

    private static string Translate(string text)
    {
        Load();
        // Spazi ai bordi (testi composti): si traducono le parole e si rimettono.
        string core = text.Trim();
        if (core.Length == 0) return text;
        string lead = text[..text.IndexOf(core[0])], trail = text[(lead.Length + core.Length)..];
        if (_exact!.TryGetValue(text, out var hit) && Pick(hit) is { } whole) return whole;
        if (_exact.TryGetValue(core, out hit) && Pick(hit) is { } trimmed) return lead + trimmed + trail;
        // Titoli di sezione scritti in maiuscolo al momento del disegno ("AVVIO").
        if (!core.Any(char.IsLower) && _upper.TryGetValue(core, out hit) && Pick(hit) is { } upper)
            return lead + upper.ToUpperInvariant() + trail;
        // Numeri dentro ("12 episodi", "Passo 2 di 4"): il modello con {0} {1}.
        var numbers = Numbers.Matches(core).Select(m => m.Value).ToList();
        if (numbers.Count > 0)
        {
            int i = 0;
            string key = Numbers.Replace(core, _ => "{" + (i++).ToString(CultureInfo.InvariantCulture) + "}");
            if (_exact.TryGetValue(key, out hit) && Pick(hit) is { } pattern)
                return lead + Regex.Replace(pattern, @"\{(\d)\}", m => int.TryParse(m.Groups[1].Value, out int n) && n < numbers.Count ? numbers[n] : m.Value) + trail;
        }
        // Modelli con parti libere (titoli, nomi): "Risultati per “{s}”".
        foreach (var (pattern, it, entry) in _templates!)
        {
            var m = pattern.Match(core);
            if (!m.Success || Pick(entry) is not { } target) continue;
            var values = m.Groups.Cast<Group>().Skip(1).Select(g => g.Value).ToList();
            // Le parti si rimettono nell'ordine in cui compaiono nel modello italiano ({s} e {n}).
            var slots = Regex.Matches(it, @"\{s\}|\{\d\}").Select(x => x.Value).ToList();
            int sIndex = 0;
            string filled = Regex.Replace(target, @"\{s\}|\{\d\}", x =>
            {
                if (x.Value == "{s}")
                {
                    int k = -1, seen = 0;
                    for (int j = 0; j < slots.Count; j++) if (slots[j] == "{s}" && seen++ == sIndex) { k = j; break; }
                    sIndex++;
                    return k >= 0 && k < values.Count ? T(values[k]) : x.Value;
                }
                int pos = slots.IndexOf(x.Value);
                return pos >= 0 && pos < values.Count ? values[pos] : x.Value;
            });
            return lead + filled + trail;
        }
        // [2026-11-22] Testi composti con il punto in mezzo ("HORROR · DRAMMA", "3 album · 34 brani · 1 h 21 min",
        // "IN LIBRERIA · 1994") o con piu' spazi ("1 brano     4 minuti"): si traduce ogni pezzo e si rimettono i
        // separatori com'erano. Se nessun pezzo cambia resta il testo originale.
        var pieces = Separators.Split(core);
        if (pieces.Length > 1)
        {
            bool changed = false;
            for (int k = 0; k < pieces.Length; k += 2)
            {
                if (pieces[k].Length == 0) continue;
                string tr = T(pieces[k]);
                if (!ReferenceEquals(tr, pieces[k]) && tr != pieces[k]) { pieces[k] = tr; changed = true; }
            }
            if (changed) return lead + string.Concat(pieces) + trail;
        }
        // Testi incollati ("Vai a " + tempo, "Scollega " + nome): la parte iniziale nota.
        foreach (var (prefix, entry) in _prefixes!)
            if (core.StartsWith(prefix, StringComparison.Ordinal) && Pick(entry) is { } start)
                return lead + start + core[prefix.Length..] + trail;
        return text;
    }
}
