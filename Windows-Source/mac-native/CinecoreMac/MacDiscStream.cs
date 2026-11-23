using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CinecoreMac;

/// <summary>
/// I VOB del titolo principale di un DVD come un unico file, per LAV Splitter nel VideoHost (madVR / MPC-VR): LAV apre un
/// solo VOB alla volta e il protocollo concat di FFmpeg non lo accetta, mentre i flussi HTTP si'. Un piccolo server sulla
/// sola interfaccia locale (127.0.0.1) unisce i file byte per byte e risponde alle richieste a intervalli (Range), cosi'
/// la ricerca nel film funziona. Gli indirizzi hanno un gettone casuale e valgono solo per la sessione dell'app.
/// </summary>
internal static class MacDiscStream
{
    private static HttpListener? _listener;
    private static int _port;
    private static readonly Dictionary<string, string[]> Sets = new(StringComparer.Ordinal);
    // Titoli letti con libdvdread (anche DVD protetti): gettone -> disco e gruppo di titoli, con il lettore aperto.
    private sealed class DvdTitle { public string Root = ""; public int TitleSet; public MacDvdRead.TitleReader? Reader; public readonly object Lock = new(); }
    private static readonly Dictionary<string, DvdTitle> Titles = new(StringComparer.Ordinal);

    /// <summary>Un titolo del DVD come file unico, letto e (se serve) decifrato da libdvdread. Null se non si apre.</summary>
    public static string? UrlForTitle(string root, int titleSet)
    {
        if (!MacDvdRead.Available || titleSet <= 0) return null;
        // Lo chiedono insieme l'analisi del file e l'avvio del motore: un solo lettore per titolo.
        lock (OpenLock)
        {
            lock (Titles)
                foreach (var (existing, t) in Titles)
                    if (t.Root == root && t.TitleSet == titleSet && t.Reader != null) return Url(existing);
            var reader = MacDvdRead.Open(root, titleSet);
            if (reader == null || reader.Blocks <= 0) { reader?.Dispose(); return null; }
            Start();
            string token = Guid.NewGuid().ToString("N");
            lock (Titles) Titles[token] = new DvdTitle { Root = root, TitleSet = titleSet, Reader = reader };
            Console.Error.WriteLine($"[disco] titolo {titleSet} con libdvdread: {reader.Length / 1048576} MB");
            return Url(token);
        }
    }

    private static readonly object OpenLock = new(), StartLock = new();
    private static string Url(string token) => $"http://127.0.0.1:{_port}/dvd/{token}.vob";

    /// <summary>Disco espulso o cambiato: i lettori aperti si chiudono.</summary>
    public static void CloseTitles(string? root = null)
    {
        lock (Titles)
            foreach (var key in new List<string>(Titles.Keys))
                if (root == null || Titles[key].Root == root)
                {
                    var t = Titles[key];
                    lock (t.Lock) { t.Reader?.Dispose(); t.Reader = null; }
                    Titles.Remove(key);
                }
    }

    public static string UrlFor(IReadOnlyList<string> files)
    {
        Start();
        string token = Guid.NewGuid().ToString("N");
        lock (Sets) Sets[token] = files.ToArray();
        return $"http://127.0.0.1:{_port}/dvd/{token}.vob";
    }

    public static int Port { get { Start(); return _port; } }
    public static void EnsureStarted() => Start();

    private static void Start()
    {
        lock (StartLock) StartCore();
    }

    private static void StartCore()
    {
        if (_listener != null) return;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        _listener = listener;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { break; }
                _ = Task.Run(() => Serve(context));
            }
        });
    }

    private static void Serve(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            string requested = context.Request.Url?.AbsolutePath ?? "";
            // Navigatore dei menu (MacDvdNav): /dvdnav/<gettone>/<generazione>.mpg, una diretta senza lunghezza.
            if (requested.StartsWith("/dvdnav/", StringComparison.Ordinal))
            {
                var parts = requested.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && MacDvdNav.ByToken(parts[1]) is { } nav && int.TryParse(Path.GetFileNameWithoutExtension(parts[2]), out int gen))
                { nav.Serve(context, gen); return; }
                response.StatusCode = 404; return;
            }
            string token = Path.GetFileNameWithoutExtension(requested);
            DvdTitle? title;
            lock (Titles) Titles.TryGetValue(token, out title);
            if (title != null) { ServeTitle(context, title); return; }
            string[]? files;
            lock (Sets) Sets.TryGetValue(token, out files);
            if (files == null) { response.StatusCode = 404; return; }
            long[] sizes = files.Select(f => new FileInfo(f).Length).ToArray();
            long total = sizes.Sum(), from = 0, to = total - 1;
            response.ContentType = "video/mpeg";
            response.AddHeader("Accept-Ranges", "bytes");
            string? range = context.Request.Headers["Range"];
            if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var parts = range[6..].Split('-');
                if (long.TryParse(parts[0], out long a)) from = a;
                if (parts.Length > 1 && long.TryParse(parts[1], out long b)) to = Math.Min(b, total - 1);
                if (from >= total) { response.StatusCode = 416; response.AddHeader("Content-Range", $"bytes */{total}"); return; }
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {from}-{to}/{total}");
            }
            response.ContentLength64 = to - from + 1;
            if (context.Request.HttpMethod == "HEAD") return;
            var buffer = new byte[1 << 20];
            long position = from, offset = 0;
            for (int i = 0; i < files.Length && position <= to; i++)
            {
                long start = offset, end = offset + sizes[i];
                offset = end;
                if (position >= end) continue;
                using var stream = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                stream.Position = position - start;
                while (position <= to && position < end)
                {
                    int want = (int)Math.Min(buffer.Length, Math.Min(to + 1, end) - position);
                    int read = stream.Read(buffer, 0, want);
                    if (read <= 0) break;
                    response.OutputStream.Write(buffer, 0, read);
                    position += read;
                }
            }
        }
        catch { /* il lettore ha chiuso la connessione (ricerca nel film): normale */ }
        finally { try { response.Close(); } catch { } }
    }

    private static void ServeTitle(HttpListenerContext context, DvdTitle title)
    {
        var response = context.Response;
        var reader = title.Reader;
        if (reader == null) { response.StatusCode = 404; return; }
        long total = reader.Length, from = 0, to = total - 1;
        response.ContentType = "video/mpeg";
        response.AddHeader("Accept-Ranges", "bytes");
        string? range = context.Request.Headers["Range"];
        if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var parts = range[6..].Split('-');
            if (long.TryParse(parts[0], out long a)) from = a;
            if (parts.Length > 1 && long.TryParse(parts[1], out long b)) to = Math.Min(b, total - 1);
            if (from >= total) { response.StatusCode = 416; response.AddHeader("Content-Range", $"bytes */{total}"); return; }
            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {from}-{to}/{total}");
        }
        response.ContentLength64 = to - from + 1;
        if (context.Request.HttpMethod == "HEAD") return;
        const int Chunk = 256; // settori per lettura (512 KB)
        var buffer = new byte[Chunk * MacDvdRead.Block];
        long position = from;
        while (position <= to)
        {
            long block = position / MacDvdRead.Block;
            int skip = (int)(position % MacDvdRead.Block);
            int blocks = (int)Math.Min(Chunk, reader.Blocks - block);
            if (blocks <= 0) break;
            int got;
            lock (title.Lock) got = title.Reader == null ? 0 : reader.Read(block, buffer, blocks);
            if (got <= 0) break;
            int count = (int)Math.Min((long)got * MacDvdRead.Block - skip, to + 1 - position);
            response.OutputStream.Write(buffer, skip, count);
            position += count;
        }
    }
}
