using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CinecorePlayer2025.Utilities
{
    internal class SubtitleNameNormalizer
    {
        // Aggiungi/edita qui: pattern (regex) -> etichetta lingua
        private static readonly (string Pattern, string Label)[] LanguagePatterns =
        {
            (@"\b(it|ita|italian|italiano)\b", "Italiano"),
            (@"\b(en|eng|english|inglese)\b", "Inglese"),
            (@"\b(fr|fra|fre|french|francese|fran[cç]ais)\b", "Francese"),
            (@"\b(es|spa|spanish|spagnolo|espa[nñ]ol)\b", "Spagnolo"),
            (@"\b(de|deu|ger|german|tedesco|deutsch)\b", "Tedesco"),
            (@"\b(pt|por|portuguese|portoghese|portugu[eê]s)\b|\bpt[-_](br|pt)\b", "Portoghese"),
            (@"\b(nl|nld|dut|dutch|olandese)\b", "Olandese"),

            (@"\b(sv|swe|swedish|svedese)\b", "Svedese"),
            (@"\b(no|nor|nob|nno|norwegian|norsk|norvegese)\b", "Norvegese"),
            (@"\b(da|dan|danish|danese)\b", "Danese"),
            (@"\b(fi|fin|finnish|finlandese)\b", "Finlandese"),

            (@"\b(pl|pol|polish|polacco)\b", "Polacco"),
            (@"\b(cs|ces|cze|czech|ceco)\b", "Ceco"),
            (@"\b(sk|slk|slo|slovak|slovacco)\b", "Slovacco"),
            (@"\b(hu|hun|hungarian|ungherese)\b", "Ungherese"),
            (@"\b(ro|ron|rum|romanian|rumeno)\b", "Rumeno"),
            (@"\b(bg|bul|bulgarian|bulgaro)\b", "Bulgaro"),
            (@"\b(el|ell|gre|greek|greco)\b|ελληνικ(?:ά|α)", "Greco"),

            (@"\b(tr|tur|turkish|turco)\b|t[uü]rk(?:çe|ce)", "Turco"),
            (@"\b(he|heb|hebrew|ebraico|ivrit)\b|עברית", "Ebraico"),
            (@"\b(ar|ara|arabic|arabo)\b|العربية", "Arabo"),
            (@"\b(fa|fas|per|persian|farsi|persiano)\b|فارسی", "Persiano"),

            (@"\b(ru|rus|russian|russo)\b|русск", "Russo"),
            (@"\b(uk|ukr|ukrainian|ucraino)\b|україн", "Ucraino"),

            (@"\b(ja|jpn|japanese|giapponese)\b|日本語", "Giapponese"),
            (@"\b(ko|kor|korean|coreano)\b|한국어", "Coreano"),
            (@"\b(zh|chi|zho|cmn|yue|chinese|cinese)\b|中文|汉语|漢語", "Cinese"),

            (@"\b(th|tha|thai|tailandese)\b|ไทย", "Thai"),
            (@"\b(vi|vie|vietnamese|vietnamita)\b|tiếng\s*việt", "Vietnamita"),
            (@"\b(id|ind|indonesian|indonesiano)\b|bahasa\s*indonesia", "Indonesiano"),
            (@"\b(ms|msa|may|malay|malese)\b|bahasa\s*melayu", "Malese"),

            (@"\b(sr|srp|serbian|serbo)\b", "Serbo"),
            (@"\b(hr|hrv|croatian|croato)\b", "Croato"),
            (@"\b(sl|slv|slovenian|sloveno)\b", "Sloveno"),
            (@"\b(et|est|estonian|estone)\b", "Estone"),
            (@"\b(lv|lav|latvian|lettone)\b", "Lettone"),
            (@"\b(lt|lit|lithuanian|lituano)\b", "Lituano"),

            (@"\b(hi|hin|hindi)\b|हिन्दी", "Hindi"),
            (@"\b(bn|ben|bengali)\b|বাংলা", "Bengali"),
            (@"\b(ta|tam|tamil)\b|தமிழ்", "Tamil"),
            (@"\b(te|tel|telugu)\b|తెలుగు", "Telugu"),
            (@"\b(ur|urd|urdu)\b|اردو", "Urdu"),

            // --- Altre lingue comuni (per librerie/track non “normalizzati”) ---
            (@"\b(ca|cat|catalan|catal[aà])\b", "Catalano"),
            (@"\b(gl|glg|galician|galego)\b", "Galiziano"),
            (@"\b(eu|eus|baq|basque|euskara)\b", "Basco"),
            (@"\b(is|isl|ice|icelandic|islandese)\b", "Islandese"),
            (@"\b(ga|gle|irish|irlandese)\b|gaeilge", "Irlandese"),
            (@"\b(cy|cym|welsh|gallese)\b|cymraeg", "Gallese"),
            (@"\b(af|afr|afrikaans)\b", "Afrikaans"),
            (@"\b(sw|swa|swahili|kiswahili)\b", "Swahili"),
            (@"\b(am|amh|amharic)\b|አማርኛ", "Amarico"),
            (@"\b(ne|nep|nepali)\b|नेपाली", "Nepalese"),
            (@"\b(si|sin|sinhala)\b|සිංහල", "Singalese"),
            (@"\b(kn|kan|kannada)\b|ಕನ್ನಡ", "Kannada"),
            (@"\b(ml|mal|malayalam)\b|മലയാളം", "Malayalam"),
            (@"\b(mr|mar|marathi)\b|मराठी", "Marathi"),
            (@"\b(gu|guj|gujarati)\b|ગુજરાતી", "Gujarati"),
            (@"\b(pa|pan|punjabi)\b|ਪੰਜਾਬੀ", "Punjabi"),
            (@"\b(jv|jav|javanese)\b|basa\s*jawa", "Giavanese"),
            (@"\b(my|mya|bur|burmese|myanmar)\b|မြန်မာ", "Burmese"),
            (@"\b(km|khm|khmer|cambodian)\b|ភាសាខ្មែរ", "Khmer"),
            (@"\b(lo|lao|laotian)\b|ລາວ", "Lao"),
            (@"\b(mn|mon|mongolian|mongolo)\b|монгол", "Mongolo"),
            (@"\b(kk|kaz|kazakh|kazako)\b|қазақ", "Kazako"),
            (@"\b(az|aze|azerbaijani|azero)\b|azərbaycan", "Azero"),
            (@"\b(ka|kat|geo|georgian|georgiano)\b|ქართული", "Georgiano"),
            (@"\b(hy|hye|arm|armenian|armeno)\b|հայերեն", "Armeno"),
            (@"\b(la|lat|latin|latino)\b", "Latino"),
            (@"\b(eo|epo|esperanto)\b", "Esperanto"),
            (@"\b(bo|tib|bod|tibetan)\b|བོད", "Tibetano"),
        };

        private static string? DetectLanguageLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            foreach (var (pattern, label) in LanguagePatterns)
            {
                if (Regex.IsMatch(raw, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return label;
            }
            return null;
        }



        private static string? LabelToLanguageKey(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return null;

            return label.Trim() switch
            {
                "Italiano" => "it",
                "Inglese" => "en",
                "Francese" => "fr",
                "Spagnolo" => "es",
                "Tedesco" => "de",
                "Portoghese" => "pt",
                "Olandese" => "nl",
                "Svedese" => "sv",
                "Norvegese" => "no",
                "Danese" => "da",
                "Finlandese" => "fi",
                "Polacco" => "pl",
                "Ceco" => "cs",
                "Slovacco" => "sk",
                "Ungherese" => "hu",
                "Rumeno" => "ro",
                "Bulgaro" => "bg",
                "Greco" => "el",
                "Turco" => "tr",
                "Ebraico" => "he",
                "Arabo" => "ar",
                "Persiano" => "fa",
                "Russo" => "ru",
                "Ucraino" => "uk",
                "Giapponese" => "ja",
                "Coreano" => "ko",
                "Cinese" => "zh",
                "Thai" => "th",
                "Vietnamita" => "vi",
                "Indonesiano" => "id",
                "Malese" => "ms",
                "Serbo" => "sr",
                "Croato" => "hr",
                "Sloveno" => "sl",
                "Estone" => "et",
                "Lettone" => "lv",
                "Lituano" => "lt",
                "Hindi" => "hi",
                "Bengali" => "bn",
                "Tamil" => "ta",
                "Telugu" => "te",
                "Urdu" => "ur",
                "Catalano" => "ca",
                "Galiziano" => "gl",
                "Basco" => "eu",
                "Islandese" => "is",
                "Irlandese" => "ga",
                "Gallese" => "cy",
                "Afrikaans" => "af",
                "Swahili" => "sw",
                "Amarico" => "am",
                "Nepalese" => "ne",
                "Singalese" => "si",
                "Kannada" => "kn",
                "Malayalam" => "ml",
                "Marathi" => "mr",
                "Gujarati" => "gu",
                "Punjabi" => "pa",
                "Giavanese" => "jv",
                "Burmese" => "my",
                "Khmer" => "km",
                "Lao" => "lo",
                "Mongolo" => "mn",
                "Kazako" => "kk",
                "Azero" => "az",
                "Georgiano" => "ka",
                "Armeno" => "hy",
                "Latino" => "la",
                "Esperanto" => "eo",
                "Tibetano" => "bo",
                _ => null
            };
        }

        public static string NormalizeSubtitleTrackName(string? raw, int fallbackIndex)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return AppLanguage.T("Sottotitoli", "Subtitles") + $" {fallbackIndex}";

            string s = raw.Trim();

            bool forced = Regex.IsMatch(s, @"\bforced\b|\bforzat[oi]\b|\bforz\b", RegexOptions.IgnoreCase);
            bool sdh = Regex.IsMatch(s, @"\bSDH\b|\bhearing\s*impaired\b|\bHI\b|\bCC\b|\bclosed\s*captions?\b", RegexOptions.IgnoreCase);

            string? lang = DetectLanguageLabel(s);
            var parts = new List<string>();
            parts.Add(!string.IsNullOrWhiteSpace(lang) ? lang : AppLanguage.T("Sottotitoli", "Subtitles") + $" {fallbackIndex}");
            if (forced) parts.Add("Forzati");
            if (sdh) parts.Add("Non udenti");

            // dedup case-insensitive
            var uniq = parts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(" • ", uniq);
        }

        /// <summary>
        /// Riduce i nomi tecnici prodotti da DirectShow/mpv a lingua e formato.
        /// Canali, bitrate, PID, flag e titoli duplicati restano volutamente fuori.
        /// </summary>
        public static string NormalizeAudioTrackName(string? raw, int fallbackIndex)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return $"Traccia audio {fallbackIndex}";

            string value = Regex.Replace(raw.Trim(), @"[_|;,]+", " ");
            string? language = DetectLanguageLabel(value);
            string format = DetectAudioFormat(value);
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(language))
                parts.Add(language);
            if (!string.IsNullOrWhiteSpace(format))
                parts.Add(format);
            if (parts.Count == 0)
                return $"Traccia audio {fallbackIndex}";
            return string.Join(" • ", parts.Distinct(StringComparer.OrdinalIgnoreCase));

            static string DetectAudioFormat(string source)
            {
                string s = source.ToLowerInvariant();
                bool atmos = Regex.IsMatch(s, @"\batmos\b");
                if (Regex.IsMatch(s, @"\bdts\s*[:._-]?\s*x\b|\bdtsx\b")) return "DTS:X";
                if (Regex.IsMatch(s, @"\btrue\s*hd\b|\btruehd\b")) return atmos ? "Dolby TrueHD Atmos" : "Dolby TrueHD";
                if (Regex.IsMatch(s, @"\bdts\s*[-._ ]?\s*hd\s*(ma|master)?\b")) return "DTS-HD MA";
                if (Regex.IsMatch(s, @"\be\s*[-._ ]?\s*ac\s*[-._ ]?\s*3\b|\bddp\b|dolby\s+digital\s+plus")) return atmos ? "Dolby Digital Plus Atmos" : "Dolby Digital Plus";
                if (Regex.IsMatch(s, @"\bac\s*[-._ ]?\s*3\b|\bdd\b|dolby\s+digital")) return "Dolby Digital";
                if (Regex.IsMatch(s, @"\bdts\b")) return "DTS";
                if (Regex.IsMatch(s, @"\blpcm\b|\bpcm\b")) return "PCM";
                if (Regex.IsMatch(s, @"\bflac\b")) return "FLAC";
                if (Regex.IsMatch(s, @"\balac\b")) return "ALAC";
                if (Regex.IsMatch(s, @"\bopus\b")) return "Opus";
                if (Regex.IsMatch(s, @"\bvorbis\b")) return "Vorbis";
                if (Regex.IsMatch(s, @"\baac\b")) return "AAC";
                if (Regex.IsMatch(s, @"\bmp3\b|mpeg\s*(?:layer\s*)?3")) return "MP3";
                return string.Empty;
            }
        }

        /// <summary>
        /// Tenta di dedurre la lingua dal nome raw del sottotitolo.
        /// Utile per raggruppare le tracce in sotto-menu per lingua.
        /// </summary>
        public static string? TryDetectLanguageLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return DetectLanguageLabel(raw);
        }

        public static string? TryDetectLanguageKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var label = DetectLanguageLabel(raw);
            return LabelToLanguageKey(label);
        }
    }
}
