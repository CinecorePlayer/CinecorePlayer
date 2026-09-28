using System.Globalization;

namespace Cinecore.AudioVideoSync;

internal static class CliOptions
{
    public static bool TryParse(string[] args, out AnalysisOptions? options, out string? error)
    {
        options = null;
        error = null;

        if (args.Length < 2 || !string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            error = "Comando atteso: analyze <file>";
            return false;
        }

        string input = Path.GetFullPath(args[1]);
        int reference = 0;
        int target = 1;
        double start = 60;
        double duration = 900;
        double maxOffset = 5;
        string? ffmpeg = null;
        string? json = null;
        string? apply = null;
        string? mkvmerge = null;
        bool force = false;

        for (int i = 2; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                error = $"Opzione non valida o senza valore: {arg}";
                return false;
            }

            string value = args[++i];
            switch (arg.ToLowerInvariant())
            {
                case "--reference":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out reference) || reference < 0)
                        error = "--reference richiede un indice audio >= 0.";
                    break;
                case "--target":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out target) || target < 0)
                        error = "--target richiede un indice audio >= 0.";
                    break;
                case "--start":
                    if (!TryPositiveDouble(value, allowZero: true, out start))
                        error = "--start richiede un numero >= 0 (secondi).";
                    break;
                case "--duration":
                    if (!TryPositiveDouble(value, allowZero: false, out duration))
                        error = "--duration richiede un numero > 0 (secondi).";
                    break;
                case "--max-offset":
                    if (!TryPositiveDouble(value, allowZero: false, out maxOffset) || maxOffset > 30)
                        error = "--max-offset deve essere compreso fra 0 e 30 secondi.";
                    break;
                case "--ffmpeg":
                    ffmpeg = value;
                    break;
                case "--json":
                    json = Path.GetFullPath(value);
                    break;
                case "--apply":
                    apply = Path.GetFullPath(value);
                    break;
                case "--mkvmerge":
                    mkvmerge = value;
                    break;
                default:
                    error = $"Opzione sconosciuta: {arg}";
                    break;
            }

            if (error != null)
                return false;
        }

        if (!File.Exists(input))
        {
            error = $"File non trovato: {input}";
            return false;
        }
        if (reference == target)
        {
            error = "La traccia di riferimento e quella target devono essere diverse.";
            return false;
        }
        if (apply != null && string.Equals(input, apply, StringComparison.OrdinalIgnoreCase))
        {
            error = "Il file di output deve essere diverso dall'originale.";
            return false;
        }

        options = new AnalysisOptions(input, reference, target, start, duration, maxOffset,
            ffmpeg, json, apply, mkvmerge, force);
        return true;
    }

    private static bool TryPositiveDouble(string value, bool allowZero, out double result)
    {
        bool parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return parsed && double.IsFinite(result) && (allowZero ? result >= 0 : result > 0);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Cinecore Audio/Video Sync

            Uso:
              Cinecore.AudioVideoSync analyze <file> [opzioni]
              Cinecore.AudioVideoSync self-test

            Opzioni:
              --reference <n>     Traccia audio già sincronizzata, indice audio (default 0)
              --target <n>        Traccia da correggere, indice audio (default 1)
              --start <secondi>   Punto iniziale dell'analisi (default 60)
              --duration <sec>    Durata massima analizzata (default 900)
              --max-offset <sec>  Offset massimo cercato, max 30 (default 5)
              --ffmpeg <path>     Percorso esplicito di ffmpeg
              --json <file>       Salva il rapporto JSON
              --apply <file.mkv>  Crea una copia MKV corretta, senza ricodifica
              --mkvmerge <path>   Percorso esplicito di mkvmerge
              --force             Permette --apply anche con confidenza bassa

            Convenzione:
              delay target +1000 ms = la traccia target arriva un secondo in ritardo
              correzione -1000 ms  = la traccia target viene anticipata di un secondo
            """);
    }
}
