using CinecorePlayer2025.Utilities;
using System.Text.Json;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

if (args.Contains("--native-read"))
{
    Check(NativeState.SecKeychainGetUserInteractionAllowed(out byte beforeInteraction) == 0, "lettura stato interazione iniziale");
    // Legge soltanto i riferimenti esistenti; nessun segreto, indirizzo o identificativo nei log.
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025");
    var references = new HashSet<string>(StringComparer.Ordinal);
    void Visit(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) foreach (var p in element.EnumerateObject()) Visit(p.Value);
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Visit(child);
        else if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value && value.StartsWith("keychain:", StringComparison.Ordinal)) references.Add(value[9..]);
    }
    foreach (var name in new[] { "jellyfin.json", "tmdb.config.json" })
    {
        string path = Path.Combine(directory, name);
        if (File.Exists(path)) { using var json = JsonDocument.Parse(File.ReadAllText(path)); Visit(json.RootElement); }
    }
    var watch = System.Diagnostics.Stopwatch.StartNew();
    for (int n = 0; n < 50; n++) Parallel.ForEach(references, account => Keychain.Get(account));
    Check(watch.Elapsed < TimeSpan.FromSeconds(10), "letture native ripetute senza attesa di consenso");
    Check(NativeState.SecKeychainGetUserInteractionAllowed(out byte afterInteraction) == 0 && beforeInteraction == afterInteraction,
        "stato interazione originale ripristinato dopo le letture");
    Console.WriteLine($"Native references={references.Count} authorizationRequired={Keychain.AccessRequired}");
    return;
}

var backend = new FakeBackend();
var session = new KeychainSession(backend);
Parallel.For(0, 100, _ => CheckQuiet(session.Get("saved") == "test-value"));
Check(backend.Reads == 1 && backend.InteractiveReads == 0, "100 letture concorrenti usano una sola lettura non interattiva");
backend.Status = -25308;
Parallel.For(0, 100, _ => session.Get("blocked"));
Check(backend.Reads == 2 && session.AccessRequired, "rifiuto memorizzato senza ripetere richieste");
session.Get("blocked-second");
backend.Status = -128;
int before = backend.Reads;
Check(!session.AuthorizePending() && backend.Reads == before + 1 && backend.InteractiveReads == 1, "annullamento ferma la coda al primo dialogo");
for (int n = 0; n < 100; n++) session.Get("blocked");
Check(backend.Reads == before + 1, "nessun nuovo tentativo automatico dopo Annulla");
backend.Status = 0;
Check(session.AuthorizePending() && !session.AccessRequired, "autorizzazione esplicita recupera le voci bloccate");
before = backend.Reads;
Check(session.Get("blocked") == "test-value" && backend.Reads == before, "valore autorizzato riusato dalla cache");
backend.WriteStatus = -25308;
Check(!session.Set("saved", "new-value") && session.Get("saved") == "test-value", "scrittura fallita conserva il valore precedente");
backend.WriteStatus = 0;
Check(session.Set("saved", "new-value") && session.Get("saved") == "new-value", "scrittura riuscita aggiorna la cache");
session.Delete("saved");
Check(session.Get("saved") == null, "eliminazione invalida la cache");
backend.Status = -25300;
var missing = new KeychainSession(backend);
missing.Get("missing");
Check(!missing.AccessRequired, "voce assente distinta da accesso negato");
Console.WriteLine("Tutti i controlli Portachiavi superati.");

void CheckQuiet(bool condition) { if (!condition) throw new Exception("Valore inatteso"); }

sealed class FakeBackend : IKeychainBackend
{
    public int Reads, InteractiveReads, Status, WriteStatus;
    public KeychainRead Read(string account, bool interactive)
    {
        Interlocked.Increment(ref Reads);
        if (interactive) Interlocked.Increment(ref InteractiveReads);
        return new(Status, Status == 0 ? "test-value" : null);
    }
    public int Write(string account, string value) => WriteStatus;
    public int Delete(string account) => 0;
}

static class NativeState
{
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/Security.framework/Security")]
    internal static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
}
