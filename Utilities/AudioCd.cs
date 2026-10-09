#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// CD audio. Il disco diventa una cartella-album per disco (un piccolo file ".cdda" per traccia, con il
    /// titolo nel nome e la copertina accanto), cosi' coda, copertine, testi e memoria degli ascolti lo
    /// trattano come un album qualsiasi e due CD diversi non si confondono. L'audio vero si legge dal
    /// lettore a settori grezzi e arriva a FFmpeg come WAV su una porta locale, con i tag dentro.
    /// Titoli, artista e copertina vengono da MusicBrainz, riconoscendo il disco dall'indice delle tracce.
    /// </summary>
    internal static class AudioCd
    {
        public const string Extension = ".cdda";
        private const int SectorBytes = 2352;
        private const int ChunkSectors = 26;
        private const string StubMagic = "CINECORE-CD";

        public sealed record Track(int Number, int StartLba, int Sectors)
        {
            public double Seconds => Sectors / 75.0;
        }

        public sealed class Disc
        {
            public char Drive;
            public string Id = "";
            public List<Track> Tracks = new();
            public string Folder = "";
            public string Album = "";
            public string Artist = "";
            public string Year = "";
            public string[] Titles = Array.Empty<string>();
            public string[] Artists = Array.Empty<string>();
            public bool Identified;
            /// <summary>Ultima volta che il lettore ha confermato di contenere questo disco.</summary>
            public long CheckedAt;
            public readonly object ReadLock = new();

            // Lettura anticipata: blocchi gia' letti (per posizione sul disco) e fin dove conviene portarsi avanti.
            public readonly Dictionary<int, byte[]> Chunks = new();
            public readonly Queue<int> ChunkOrder = new();
            public readonly AutoResetEvent Wake = new(false);
            public int AheadFrom;
            public long ListenedAt;
            public int Waiting;
            public int EndLba;
            public bool Gone;
            public Thread? Reader;
            public SafeFileHandle? Handle;

            public string TitleOf(int index) => index < Titles.Length && Titles[index].Length > 0 ? Titles[index] : "";
            public string ArtistOf(int index) => index < Artists.Length && Artists[index].Length > 0 ? Artists[index] : Artist;
        }

        private sealed class SavedDisc
        {
            public string? Album { get; set; }
            public string? Artist { get; set; }
            public string? Year { get; set; }
            public string[]? Titles { get; set; }
            public string[]? Artists { get; set; }
        }

        private static readonly object Sync = new();
        private static readonly Dictionary<string, Disc> Discs = new(StringComparer.Ordinal);
        private static readonly HttpClient Http = CreateClient();
        private static TcpListener? _listener;
        private static readonly string Token = Guid.NewGuid().ToString("N");

        private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinecorePlayer2025", "audio-cd");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            // MusicBrainz chiede un User-Agent che dica chi e' il programma.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CinecorePlayer/0.3 (https://github.com/CinecorePlayer/CinecorePlayer)");
            return client;
        }

        // ----- riconoscere un CD audio -----

        /// <summary>Vero per la radice di un lettore con un CD audio ("I:\") o per una sua traccia ("I:\Track03.cda").</summary>
        public static bool IsAudioCdPath(string? path, out char drive, out int track)
        {
            drive = '\0'; track = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path!.Length < 2 || path[1] != ':' || path.Contains("://", StringComparison.Ordinal)) return false;
                string rest = path.Length > 3 ? path[3..] : "";
                if (rest.Length > 0)
                {
                    if (!rest.EndsWith(".cda", StringComparison.OrdinalIgnoreCase) || rest.Contains('\\')) return false;
                    string digits = new string(rest.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out track)) return false;
                }
                var info = new DriveInfo(path[..2] + "\\");
                if (info.DriveType != DriveType.CDRom || !info.IsReady) return false;
                if (!string.Equals(info.DriveFormat, "CDFS", StringComparison.OrdinalIgnoreCase)) return false;
                if (rest.Length == 0 && !Directory.EnumerateFiles(info.Name, "*.cda").Any()) return false;
                drive = char.ToUpperInvariant(path[0]);
                return true;
            }
            catch { return false; }
        }

        public static bool IsStub(string? path) => path != null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        // ----- lettore -----

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref RawReadInfo input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential)]
        private struct RawReadInfo
        {
            public long DiskOffset;
            public uint SectorCount;
            public uint TrackMode;
        }

        private static SafeFileHandle OpenDrive(char drive) =>
            CreateFileW(@"\\.\" + drive + ":", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

        /// <summary>Indice del disco: le tracce audio e l'impronta con cui MusicBrainz lo riconosce.</summary>
        private static Disc? ReadToc(char drive)
        {
            using var handle = OpenDrive(drive);
            if (handle.IsInvalid) return null;
            byte[] toc = new byte[804];
            if (!DeviceIoControl(handle, 0x00024000, IntPtr.Zero, 0, toc, toc.Length, out _, IntPtr.Zero)) return null;
            int first = toc[2], last = toc[3];
            var entries = new List<(int Number, bool Data, int Lba)>();
            for (int i = 0; i <= last - first + 1 && 12 + i * 8 <= toc.Length; i++)
            {
                int at = 4 + i * 8;
                entries.Add((toc[at + 2], (toc[at + 1] & 0x04) != 0, (toc[at + 5] * 60 + toc[at + 6]) * 75 + toc[at + 7] - 150));
            }
            if (entries.Count < 2) return null;
            var disc = new Disc { Drive = drive };
            int leadOut = entries[^1].Lba;
            for (int i = 0; i < entries.Count - 1; i++)
            {
                if (entries[i].Data)
                {
                    // CD con una sessione dati in coda: l'audio finisce 11400 settori prima.
                    if (disc.Tracks.Count > 0) leadOut = entries[i].Lba - 11400;
                    continue;
                }
                disc.Tracks.Add(new Track(entries[i].Number, entries[i].Lba, entries[i + 1].Lba - entries[i].Lba));
            }
            if (disc.Tracks.Count == 0) return null;
            disc.EndLba = leadOut;
            if (leadOut < entries[^1].Lba)
            {
                var tail = disc.Tracks[^1];
                disc.Tracks[^1] = tail with { Sectors = Math.Max(1, leadOut - tail.StartLba) };
            }

            // Impronta MusicBrainz: SHA-1 di prima traccia, ultima, fine disco e 99 posizioni, in esadecimale.
            var text = new StringBuilder();
            text.Append(disc.Tracks[0].Number.ToString("X2")).Append(disc.Tracks[^1].Number.ToString("X2")).Append((leadOut + 150).ToString("X8"));
            for (int number = 1; number <= 99; number++)
            {
                var track = disc.Tracks.FirstOrDefault(t => t.Number == number);
                text.Append((track == null ? 0 : track.StartLba + 150).ToString("X8"));
            }
            disc.Id = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString()))).Replace('+', '.').Replace('/', '_').Replace('=', '-');
            disc.Folder = Path.Combine(Root, disc.Id);
            return disc;
        }

        private static string TocQuery(Disc disc)
        {
            var last = disc.Tracks[^1];
            return disc.Tracks[0].Number + "+" + last.Number + "+" + (last.StartLba + last.Sectors + 150) + "+" + string.Join("+", disc.Tracks.Select(t => t.StartLba + 150));
        }

        /// <summary>Legge settori audio grezzi. Un settore che non si legge (graffio) diventa silenzio, senza fermare il brano.</summary>
        /// <returns>Quanti settori non si sono letti.</returns>
        private static int ReadSectors(SafeFileHandle handle, int lba, int count, byte[] buffer)
        {
            var request = new RawReadInfo { DiskOffset = (long)lba * 2048, SectorCount = (uint)count, TrackMode = 2 };
            for (int attempt = 0; attempt < 3; attempt++)
                if (DeviceIoControl(handle, 0x0002403E, ref request, Marshal.SizeOf<RawReadInfo>(), buffer, count * SectorBytes, out int got, IntPtr.Zero) && got == count * SectorBytes)
                    return 0;
            if (count == 1) { Array.Clear(buffer, 0, SectorBytes); return 1; }
            byte[] one = new byte[SectorBytes];
            int failed = 0;
            for (int i = 0; i < count; i++)
            {
                failed += ReadSectors(handle, lba + i, 1, one);
                Buffer.BlockCopy(one, 0, buffer, i * SectorBytes, SectorBytes);
            }
            return failed;
        }

        // Mezzo minuto di musica letto in anticipo (i brani sono uno di seguito all'altro sul disco, quindi copre
        // anche l'inizio del successivo: il passaggio senza pause non aspetta il lettore) e due minuti tenuti in memoria.
        private const int AheadChunks = 90, KeptChunks = 420;

        /// <summary>Un blocco di settori per posizione sul disco; null se il disco non c'e' piu'.</summary>
        private static byte[]? Chunk(Disc disc, int index)
        {
            byte[]? data;
            lock (disc.Chunks)
            {
                if (disc.Gone) return null;
                disc.Chunks.TryGetValue(index, out data);
                disc.AheadFrom = index + 1;
                disc.ListenedAt = Environment.TickCount64;
                if (disc.Reader == null || !disc.Reader.IsAlive)
                {
                    disc.Reader = new Thread(() => ReadAhead(disc)) { IsBackground = true, Name = "Cinecore audio CD read-ahead" };
                    disc.Reader.Start();
                }
            }
            if (data == null)
            {
                long started = Environment.TickCount64;
                Interlocked.Increment(ref disc.Waiting);
                try { data = ReadChunk(disc, index); }
                finally { Interlocked.Decrement(ref disc.Waiting); }
                long took = Environment.TickCount64 - started;
                if (took > 400) Dbg.Log($"[CD] waited {took} ms for the drive at sector {index * ChunkSectors}", Dbg.LogLevel.Info);
            }
            disc.Wake.Set();
            return data;
        }

        private static byte[]? ReadChunk(Disc disc, int index)
        {
            lock (disc.ReadLock)
            {
                lock (disc.Chunks)
                {
                    if (disc.Gone) return null;
                    if (disc.Chunks.TryGetValue(index, out var ready)) return ready;
                }
                int lba = index * ChunkSectors;
                int count = Math.Min(ChunkSectors, disc.EndLba - lba);
                if (count <= 0) return new byte[ChunkSectors * SectorBytes];
                if (disc.Handle == null || disc.Handle.IsInvalid || disc.Handle.IsClosed) disc.Handle = OpenDrive(disc.Drive);
                byte[] buffer = new byte[ChunkSectors * SectorBytes];
                int failed = disc.Handle.IsInvalid ? count : ReadSectors(disc.Handle, lba, count, buffer);
                // Niente di leggibile: se il disco non e' piu' nel lettore si smette, invece di suonare silenzio fino in fondo.
                if (failed == count && ReadToc(disc.Drive)?.Id != disc.Id)
                {
                    Dbg.Warn("[CD] the disc is no longer in the drive");
                    lock (disc.Chunks) { disc.Gone = true; disc.Chunks.Clear(); disc.ChunkOrder.Clear(); }
                    lock (Sync) Discs.Remove(disc.Id);
                    try { disc.Handle.Dispose(); } catch { }
                    return null;
                }
                lock (disc.Chunks)
                {
                    disc.Chunks[index] = buffer;
                    disc.ChunkOrder.Enqueue(index);
                    while (disc.ChunkOrder.Count > KeptChunks) disc.Chunks.Remove(disc.ChunkOrder.Dequeue());
                }
                return buffer;
            }
        }

        private static void ReadAhead(Disc disc)
        {
            long idleSince = Environment.TickCount64;
            while (true)
            {
                int next = -1;
                lock (disc.Chunks)
                {
                    if (disc.Gone) return;
                    int last = (disc.EndLba - 1) / ChunkSectors;
                    for (int i = disc.AheadFrom; i < disc.AheadFrom + AheadChunks && i <= last; i++)
                        if (!disc.Chunks.ContainsKey(i)) { next = i; break; }
                }
                if (next >= 0)
                {
                    if (ReadChunk(disc, next) == null) return;
                    idleSince = Environment.TickCount64;
                    continue;
                }
                if (!disc.Wake.WaitOne(1000) && Environment.TickCount64 - idleSince > 90000)
                {
                    // Nessuno ascolta da un minuto e mezzo: si lascia libero il lettore (e l'espulsione del disco).
                    lock (disc.ReadLock) { try { disc.Handle?.Dispose(); } catch { } disc.Handle = null; }
                    lock (disc.Chunks) { disc.Chunks.Clear(); disc.ChunkOrder.Clear(); disc.Reader = null; }
                    return;
                }
            }
        }

        // ----- l'album sul disco fisso -----

        /// <summary>
        /// Prepara l'album del CD nel lettore: indice, titoli (salvati da una volta precedente o chiesti a
        /// MusicBrainz) e i file delle tracce. Restituisce i percorsi da mettere in coda, in ordine.
        /// </summary>
        public static IReadOnlyList<string> Prepare(char drive, CancellationToken ct)
        {
            Disc? disc = ReadToc(drive);
            if (disc == null) return Array.Empty<string>();
            // Disco gia' aperto: resta il suo oggetto (con la lettura anticipata e il lettore in uso) e restano i suoi
            // file. Ricrearlo mentre un brano suona lasciava due lettori sullo stesso disco, che si contendevano la
            // testina fino a bloccare la riproduzione.
            lock (Sync)
            {
                if (Discs.TryGetValue(disc.Id, out Disc? known) && !known.Gone)
                {
                    known.CheckedAt = Environment.TickCount64;
                    try
                    {
                        var existing = Directory.EnumerateFiles(known.Folder, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                        if (existing.Count == known.Tracks.Count) return existing;
                    }
                    catch { }
                    disc = known;
                }
                else Discs[disc.Id] = disc;
            }
            disc.CheckedAt = Environment.TickCount64;
            Directory.CreateDirectory(disc.Folder);
            if (!LoadSaved(disc))
            {
                try { Identify(disc, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Dbg.Warn("[CD] MusicBrainz: " + ex.Message); }
            }
            var paths = WriteStubs(disc);
            Dbg.Log($"[CD] disc {disc.Id}: {disc.Tracks.Count} tracks, identified={disc.Identified}, album='{disc.Album}', artist='{disc.Artist}'", Dbg.LogLevel.Info);
            return paths;
        }

        private static bool LoadSaved(Disc disc)
        {
            try
            {
                string file = Path.Combine(disc.Folder, "disc.json");
                if (!File.Exists(file)) return false;
                var saved = JsonSerializer.Deserialize<SavedDisc>(File.ReadAllText(file));
                if (saved?.Titles == null || saved.Titles.Length == 0) return false;
                disc.Album = saved.Album ?? ""; disc.Artist = saved.Artist ?? ""; disc.Year = saved.Year ?? "";
                disc.Titles = saved.Titles; disc.Artists = saved.Artists ?? Array.Empty<string>();
                disc.Identified = true;
                return true;
            }
            catch { return false; }
        }

        private static void Identify(Disc disc, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(9));
            const string include = "fmt=json&inc=artist-credits+recordings";
            string? json = Get("https://musicbrainz.org/ws/2/discid/" + Uri.EscapeDataString(disc.Id) + "?" + include + "&toc=" + TocQuery(disc), timeout.Token);
            if (json == null) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return;

            foreach (var release in releases.EnumerateArray())
            {
                if (!release.TryGetProperty("media", out var media)) continue;
                JsonElement? chosen = null;
                foreach (var medium in media.EnumerateArray())
                {
                    bool sameDisc = medium.TryGetProperty("discs", out var discs) && discs.EnumerateArray().Any(d => d.TryGetProperty("id", out var id) && id.GetString() == disc.Id);
                    bool sameCount = medium.TryGetProperty("tracks", out var tracks) && tracks.GetArrayLength() == disc.Tracks.Count;
                    if (sameDisc && sameCount) { chosen = medium; break; }
                    if (sameCount) chosen ??= medium;
                }
                if (chosen is not { } found) continue;

                disc.Album = release.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
                disc.Artist = Credit(release);
                string date = release.TryGetProperty("date", out var d2) ? d2.GetString() ?? "" : "";
                disc.Year = date.Length >= 4 ? date[..4] : "";
                var titles = new List<string>(); var artists = new List<string>();
                foreach (var track in found.GetProperty("tracks").EnumerateArray())
                {
                    titles.Add(track.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "");
                    string by = Credit(track);
                    if (by.Length == 0 && track.TryGetProperty("recording", out var recording)) by = Credit(recording);
                    artists.Add(by);
                }
                disc.Titles = titles.ToArray(); disc.Artists = artists.ToArray();
                disc.Identified = true;
                try
                {
                    File.WriteAllText(Path.Combine(disc.Folder, "disc.json"), JsonSerializer.Serialize(new SavedDisc
                    { Album = disc.Album, Artist = disc.Artist, Year = disc.Year, Titles = disc.Titles, Artists = disc.Artists }));
                }
                catch { }
                string? releaseId = release.TryGetProperty("id", out var rid) ? rid.GetString() : null;
                if (releaseId != null) FetchCover(disc, releaseId, timeout.Token);
                return;
            }
        }

        private static string Credit(JsonElement owner)
        {
            if (!owner.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
            var text = new StringBuilder();
            foreach (var part in credit.EnumerateArray())
            {
                text.Append(part.TryGetProperty("name", out var name) ? name.GetString() : "");
                text.Append(part.TryGetProperty("joinphrase", out var join) ? join.GetString() : "");
            }
            return text.ToString().Trim();
        }

        private static string? Get(string url, CancellationToken ct)
        {
            using var response = Http.GetAsync(url, ct).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            return response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        }

        private static void FetchCover(Disc disc, string releaseId, CancellationToken ct)
        {
            try
            {
                string target = Path.Combine(disc.Folder, "cover.jpg");
                if (File.Exists(target)) return;
                using var response = Http.GetAsync("https://coverartarchive.org/release/" + releaseId + "/front-500", ct).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) return;
                byte[] image = response.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
                if (image.Length > 2000) File.WriteAllBytes(target, image);
            }
            catch (Exception ex) { Dbg.Log("[CD] cover: " + ex.Message, Dbg.LogLevel.Info); }
        }

        private static List<string> WriteStubs(Disc disc)
        {
            foreach (string old in Directory.EnumerateFiles(disc.Folder, "*" + Extension))
                try { File.Delete(old); } catch { }
            var paths = new List<string>();
            for (int i = 0; i < disc.Tracks.Count; i++)
            {
                string title = disc.TitleOf(i);
                if (title.Length == 0) title = (AppLanguage.English ? "Track " : "Traccia ") + disc.Tracks[i].Number;
                string name = disc.Tracks[i].Number.ToString("00") + " " + string.Concat(title.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c)).Trim();
                if (name.Length > 120) name = name[..120].Trim();
                string path = Path.Combine(disc.Folder, name + Extension);
                File.WriteAllText(path, StubMagic + "\n" + disc.Id + "\n" + i + "\n" + disc.Tracks[i].Number + "\n" +
                    disc.Tracks[i].Seconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                paths.Add(path);
            }
            return paths;
        }

        /// <summary>
        /// Titolo, artista, album e durata di una traccia, letti dal suo file e dai dati salvati del disco:
        /// senza toccare il lettore, che durante l'ascolto serve al brano in corso.
        /// </summary>
        public static bool TryTags(string? path, out string title, out string artist, out string album, out int number, out int count, out double seconds)
        {
            title = artist = album = ""; number = count = 0; seconds = 0;
            if (!IsStub(path)) return false;
            try
            {
                string[] lines = File.ReadAllLines(path!);
                if (lines.Length < 5 || lines[0] != StubMagic || !int.TryParse(lines[2], out int index)) return false;
                int.TryParse(lines[3], out number);
                double.TryParse(lines[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds);
                string folder = Path.GetDirectoryName(path!) ?? "";
                var disc = new Disc { Folder = folder };
                LoadSaved(disc);
                count = Directory.EnumerateFiles(folder, "*" + Extension).Count();
                title = disc.TitleOf(index);
                if (title.Length == 0) title = (AppLanguage.English ? "Track " : "Traccia ") + number;
                artist = disc.ArtistOf(index);
                album = disc.Album.Length > 0 ? disc.Album : (AppLanguage.English ? "Audio CD" : "CD audio");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Copia una traccia in un file WAV temporaneo, per gli strumenti esterni che vogliono un file vero
        /// (la sincronizzazione dei testi). Legge a raffiche e poi lascia il lettore al brano in ascolto, che ha
        /// mezzo minuto di riserva: la musica non si interrompe. Null se il disco non c'e'.
        /// </summary>
        public static string? ExportForAnalysis(string stubPath, CancellationToken ct)
        {
            try
            {
                string[] lines = File.ReadAllLines(stubPath);
                if (lines.Length < 3 || lines[0] != StubMagic || !int.TryParse(lines[2], out int index)) return null;
                Disc? disc = Find(lines[1]);
                if (disc == null || index < 0 || index >= disc.Tracks.Count) return null;
                Track track = disc.Tracks[index];
                string target = Path.Combine(Path.GetTempPath(), "cinecore-cd-" + Guid.NewGuid().ToString("N") + ".wav");
                long started = Environment.TickCount64;
                try
                {
                    using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                    file.Write(WavHeader(disc, index));
                    byte[] buffer = new byte[ChunkSectors * SectorBytes];
                    int sector = 0;
                    while (sector < track.Sectors)
                    {
                        ct.ThrowIfCancellationRequested();
                        // La musica viene prima: se il brano in ascolto aspetta il lettore, o ha meno di una decina di
                        // secondi di riserva (appena aperto, appena riaperto su un'altra uscita, dopo un salto), la copia
                        // si ferma e lascia il lettore a lui.
                        if (ListenerNeedsDrive(disc)) { disc.Wake.Set(); Thread.Sleep(250); continue; }
                        lock (disc.ReadLock)
                        {
                            if (disc.Gone) throw new IOException("disc removed");
                            if (disc.Handle == null || disc.Handle.IsInvalid || disc.Handle.IsClosed) disc.Handle = OpenDrive(disc.Drive);
                            long burst = Environment.TickCount64;
                            // Fino a sei secondi di lettura continua, poi si cede il passo.
                            while (sector < track.Sectors && Environment.TickCount64 - burst < 6000 && Volatile.Read(ref disc.Waiting) == 0)
                            {
                                int count = Math.Min(ChunkSectors, track.Sectors - sector);
                                if (ReadSectors(disc.Handle, track.StartLba + sector, count, buffer) == count && ReadToc(disc.Drive)?.Id != disc.Id)
                                    throw new IOException("disc removed");
                                file.Write(buffer, 0, count * SectorBytes);
                                sector += count;
                            }
                        }
                        disc.Wake.Set();
                        if (sector < track.Sectors) Thread.Sleep(Volatile.Read(ref disc.Waiting) > 0 ? 400 : 2500);
                    }
                }
                catch
                {
                    try { File.Delete(target); } catch { }
                    throw;
                }
                Dbg.Log($"[CD] track {track.Number} copied for analysis in {(Environment.TickCount64 - started) / 1000.0:0.0}s", Dbg.LogLevel.Info);
                return target;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Dbg.Warn("[CD] export: " + ex.Message); return null; }
        }

        /// <summary>Vero se qualcuno sta ascoltando e il lettore gli serve adesso: aspetta un blocco, o ne ha pochi pronti davanti.</summary>
        private static bool ListenerNeedsDrive(Disc disc)
        {
            if (Volatile.Read(ref disc.Waiting) > 0) return true;
            lock (disc.Chunks)
            {
                if (disc.Gone || Environment.TickCount64 - disc.ListenedAt > 3000) return false;
                int last = (disc.EndLba - 1) / ChunkSectors;
                for (int i = disc.AheadFrom; i < disc.AheadFrom + 30 && i <= last; i++)
                    if (!disc.Chunks.ContainsKey(i)) return true;
                return false;
            }
        }

        /// <summary>Anno del disco a cui appartiene questa traccia, se MusicBrainz lo ha dato.</summary>
        public static int? YearOf(string? path)
        {
            try
            {
                if (!IsStub(path)) return null;
                var disc = new Disc { Folder = Path.GetDirectoryName(path!) ?? "" };
                return LoadSaved(disc) && int.TryParse(disc.Year, out int year) && year > 1900 ? year : null;
            }
            catch { return null; }
        }

        // ----- FFmpeg: dal file della traccia all'audio del disco -----

        /// <summary>Per un file ".cdda" restituisce l'indirizzo locale da cui leggere l'audio; ogni altro percorso resta com'e'.</summary>
        public static string MapForPlayback(string path)
        {
            if (!IsStub(path)) return path;
            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 3 || lines[0] != StubMagic || !int.TryParse(lines[2], out int index)) return path;
                Disc? disc = Find(lines[1]);
                if (disc == null || index < 0 || index >= disc.Tracks.Count) return path;
                return "http://127.0.0.1:" + EnsureServer() + "/" + Token + "/" + disc.Id + "/" + index + ".wav";
            }
            catch (Exception ex) { Dbg.Warn("[CD] map: " + ex.Message); return path; }
        }

        /// <summary>Il disco con questa impronta, se e' in uno dei lettori (anche dopo un riavvio del programma).</summary>
        private static Disc? Find(string id)
        {
            Disc? known;
            lock (Sync) Discs.TryGetValue(id, out known);
            if (known != null)
            {
                if (Environment.TickCount64 - known.CheckedAt < 4000) return known;
                var still = ReadToc(known.Drive);
                if (still?.Id == id) { known.CheckedAt = Environment.TickCount64; return known; }
                lock (Sync) Discs.Remove(id);
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                var disc = ReadToc(char.ToUpperInvariant(drive.Name[0]));
                if (disc?.Id != id) continue;
                LoadSaved(disc);
                disc.CheckedAt = Environment.TickCount64;
                lock (Sync)
                {
                    // Un altro thread puo' averlo registrato nel frattempo: vale il primo, uno solo per disco.
                    if (Discs.TryGetValue(id, out Disc? raced) && !raced.Gone) return raced;
                    Discs[id] = disc;
                }
                return disc;
            }
            return null;
        }

        private static int EnsureServer()
        {
            lock (Sync)
            {
                if (_listener != null) return ((IPEndPoint)_listener.LocalEndpoint).Port;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var listener = _listener;
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient client;
                        try { client = listener.AcceptTcpClient(); } catch { return; }
                        ThreadPool.QueueUserWorkItem(_ => { try { Serve(client); } catch { } finally { try { client.Dispose(); } catch { } } });
                    }
                }) { IsBackground = true, Name = "Cinecore audio CD" };
                thread.Start();
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }
        }

        private static byte[] WavHeader(Disc disc, int index)
        {
            long pcm = (long)disc.Tracks[index].Sectors * SectorBytes;
            using var info = new MemoryStream();
            void Tag(string id, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                byte[] text = Encoding.UTF8.GetBytes(value + "\0");
                info.Write(Encoding.ASCII.GetBytes(id)); info.Write(BitConverter.GetBytes(text.Length)); info.Write(text);
                if (text.Length % 2 == 1) info.WriteByte(0);
            }
            info.Write(Encoding.ASCII.GetBytes("INFO"));
            string title = disc.TitleOf(index);
            Tag("INAM", title.Length > 0 ? title : (AppLanguage.English ? "Track " : "Traccia ") + disc.Tracks[index].Number);
            Tag("IART", disc.ArtistOf(index));
            Tag("IPRD", disc.Album.Length > 0 ? disc.Album : "CD audio");
            Tag("IPRT", disc.Tracks[index].Number.ToString());
            Tag("ICRD", disc.Year);
            byte[] list = info.ToArray();

            using var header = new MemoryStream();
            using var w = new BinaryWriter(header);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((uint)Math.Min(uint.MaxValue, 4 + 24 + 8 + list.Length + 8 + pcm));
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(176400); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("LIST")); w.Write(list.Length); w.Write(list);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write((uint)pcm);
            w.Flush();
            return header.ToArray();
        }

        private static void Serve(TcpClient client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 8000;
            var request = new StringBuilder();
            byte[] one = new byte[1];
            while (request.Length < 8192 && !request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (stream.Read(one, 0, 1) != 1) return;
                request.Append((char)one[0]);
            }
            string[] lines = request.ToString().Split("\r\n");
            string[] first = lines[0].Split(' ');
            string[] parts = first.Length > 1 ? first[1].Trim('/').Split('/') : Array.Empty<string>();
            Disc? disc = null;
            int index = -1;
            if (parts.Length == 3 && parts[0] == Token && int.TryParse(Path.GetFileNameWithoutExtension(parts[2]), out index))
                lock (Sync) Discs.TryGetValue(parts[1], out disc);
            if (disc == null || index < 0 || index >= disc.Tracks.Count)
            {
                Send(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            byte[] header = WavHeader(disc, index);
            Track track = disc.Tracks[index];
            long total = header.Length + (long)track.Sectors * SectorBytes;
            long from = 0;
            string? range = lines.FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            if (range != null)
            {
                string value = range[(range.IndexOf('=') + 1)..].Trim();
                long.TryParse(value.Split('-')[0], out from);
                from = Math.Clamp(from, 0, total);
            }
            Send(stream, (range != null ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + (total - 1) + "/" + total + "\r\n" : "HTTP/1.1 200 OK\r\n") +
                "Content-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: " + (total - from) + "\r\nConnection: close\r\n\r\n");
            if (first[0] == "HEAD") return;

            if (from < header.Length) stream.Write(header, (int)from, header.Length - (int)from);
            long position = Math.Max(0, from - header.Length);
            long end = (long)track.Sectors * SectorBytes;
            while (position < end)
            {
                long absolute = (long)track.StartLba * SectorBytes + position;
                int block = (int)(absolute / (ChunkSectors * SectorBytes));
                byte[]? chunk = Chunk(disc, block);
                if (chunk == null) return;
                int skip = (int)(absolute - (long)block * ChunkSectors * SectorBytes);
                int length = (int)Math.Min(chunk.Length - skip, end - position);
                stream.Write(chunk, skip, length);
                position += length;
            }
        }

        private static void Send(NetworkStream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
