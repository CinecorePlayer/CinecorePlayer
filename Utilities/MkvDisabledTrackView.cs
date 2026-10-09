#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Some Matroska remuxes mark every video track as "not enabled" (FlagEnabled = 0). LAV
    /// Splitter obeys the flag and exposes no video pin at all, so no DirectShow renderer can
    /// show the film, while players with their own splitter ignore it. The file may sit on a
    /// read-only disk and be tens of gigabytes, so it cannot be rewritten: this serves it to
    /// the splitter over loopback, byte for byte, with only those one-byte flags turned on.
    /// </summary>
    internal sealed class MkvDisabledTrackView : IDisposable
    {
        private const int HeaderBytes = 1024 * 1024;
        private const int QuickHeaderBytes = 128 * 1024;

        /// <summary>True when the bytes read already contain the track headers or the first cluster (nothing more to look for).</summary>
        private static bool HasTracksOrCluster(byte[] data)
        {
            int position = 0;
            if (!ReadElement(data, ref position, data.Length, out uint id, out int start, out long size) || id != 0x1A45DFA3 || size < 0) return true;
            position = (int)Math.Min(data.Length, start + size);
            if (!ReadElement(data, ref position, data.Length, out id, out start, out size) || id != IdSegment) return true;
            position = start;
            while (ReadElement(data, ref position, data.Length, out id, out start, out size))
            {
                if (id == IdCluster || size < 0) return true;
                if (id == IdTracks) return start + size <= data.Length;
                if (start + size >= data.Length) return false;
                position = (int)(start + size);
            }
            return false;
        }
        private const uint IdSegment = 0x18538067, IdTracks = 0x1654AE6B, IdCluster = 0x1F43B675;
        private const uint IdTrackEntry = 0xAE, IdTrackType = 0x83, IdFlagEnabled = 0xB9;

        private readonly string _path;
        private readonly long _length;
        private readonly long[] _patchOffsets;
        private readonly string _token = Guid.NewGuid().ToString("N");
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly HashSet<Socket> _clients = new();

        public string Url { get; }

        private MkvDisabledTrackView(string path, long length, long[] patchOffsets)
        {
            _path = path;
            _length = length;
            _patchOffsets = patchOffsets;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{_token}.mkv";
            _ = Task.Run(AcceptLoopAsync);
        }

        /// <summary>A view of the file with its disabled tracks enabled, or null when the file needs none.</summary>
        public static MkvDisabledTrackView? TryCreate(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.Contains("://", StringComparison.Ordinal)) return null;
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mk3d", StringComparison.OrdinalIgnoreCase)) return null;
                byte[] header;
                long length;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    length = file.Length;
                    header = new byte[(int)Math.Min(QuickHeaderBytes, length)];
                    int read = 0;
                    while (read < header.Length)
                    {
                        int n = file.Read(header, read, header.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < header.Length) Array.Resize(ref header, read);
                }
                long[] offsets = FindDisabledTrackFlags(header);
                if (offsets.Length == 0 && header.Length == QuickHeaderBytes && !HasTracksOrCluster(header))
                {
                    // Intestazione insolitamente lunga (molti allegati prima delle tracce): si rilegge di piu'.
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    header = new byte[(int)Math.Min(HeaderBytes, length)];
                    int read = 0;
                    while (read < header.Length)
                    {
                        int n = file.Read(header, read, header.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < header.Length) Array.Resize(ref header, read);
                    offsets = FindDisabledTrackFlags(header);
                }
                if (offsets.Length == 0) return null;
                var view = new MkvDisabledTrackView(path, length, offsets);
                Dbg.Log($"[MKV] '{Path.GetFileName(path)}': {offsets.Length} track(s) flagged as not enabled; served to the splitter with the flag on.", Dbg.LogLevel.Info);
                return view;
            }
            catch (Exception ex)
            {
                Dbg.Warn("[MKV] disabled-track check failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Offsets of the FlagEnabled bytes to turn on. Only when every track of a kind (video,
        /// audio) is disabled: with one enabled track the flag is a deliberate choice of the
        /// file's author and the splitter already has something to show.
        /// </summary>
        internal static long[] FindDisabledTrackFlags(byte[] data)
        {
            int position = 0;
            // EBML header, then the segment.
            if (!ReadElement(data, ref position, data.Length, out uint id, out int start, out long size) || id != 0x1A45DFA3 || size < 0) return Array.Empty<long>();
            position = (int)Math.Min(data.Length, start + size);
            if (!ReadElement(data, ref position, data.Length, out id, out start, out size) || id != IdSegment) return Array.Empty<long>();

            position = start;
            int segmentEnd = data.Length;
            var result = new List<long>();
            while (ReadElement(data, ref position, segmentEnd, out id, out start, out size))
            {
                if (id == IdCluster || size < 0) break;
                long end = start + size;
                if (id == IdTracks && end <= data.Length)
                {
                    var disabled = new Dictionary<int, List<long>>();
                    var total = new Dictionary<int, int>();
                    int trackPosition = start;
                    while (ReadElement(data, ref trackPosition, (int)end, out uint trackId, out int trackStart, out long trackSize))
                    {
                        if (trackSize < 0) return Array.Empty<long>();
                        int trackEnd = (int)Math.Min(end, trackStart + trackSize);
                        if (trackId == IdTrackEntry)
                        {
                            int type = 0;
                            long flagOffset = -1;
                            int field = trackStart;
                            while (ReadElement(data, ref field, trackEnd, out uint fieldId, out int fieldStart, out long fieldSize))
                            {
                                if (fieldSize < 0) return Array.Empty<long>();
                                if (fieldId == IdTrackType && fieldSize >= 1 && fieldSize <= 8)
                                    type = data[fieldStart + (int)fieldSize - 1];
                                else if (fieldId == IdFlagEnabled && fieldSize >= 1 && fieldSize <= 8)
                                {
                                    bool zero = true;
                                    for (int i = 0; i < fieldSize; i++) zero &= data[fieldStart + i] == 0;
                                    if (zero) flagOffset = fieldStart + fieldSize - 1;
                                }
                                field = (int)Math.Min(trackEnd, fieldStart + fieldSize);
                            }
                            if (type is 1 or 2)
                            {
                                total[type] = total.GetValueOrDefault(type) + 1;
                                if (flagOffset >= 0)
                                {
                                    if (!disabled.TryGetValue(type, out var list)) disabled[type] = list = new List<long>();
                                    list.Add(flagOffset);
                                }
                            }
                        }
                        trackPosition = trackEnd;
                    }
                    foreach (var pair in disabled)
                        if (pair.Value.Count == total.GetValueOrDefault(pair.Key))
                            result.AddRange(pair.Value);
                    break;
                }
                if (end >= data.Length) break;
                position = (int)end;
            }
            result.Sort();
            return result.ToArray();
        }

        // One EBML element header at "position"; size -1 means "unknown size".
        private static bool ReadElement(byte[] data, ref int position, int limit, out uint id, out int dataStart, out long size)
        {
            id = 0; dataStart = 0; size = 0;
            if (position >= limit || position >= data.Length) return false;
            int idLength = LeadingLength(data[position]);
            if (idLength > 4 || position + idLength >= data.Length) return false;
            for (int i = 0; i < idLength; i++) id = (id << 8) | data[position + i];
            int p = position + idLength;
            int sizeLength = LeadingLength(data[p]);
            if (sizeLength > 8 || p + sizeLength > data.Length) return false;
            long value = data[p] & (0xFF >> sizeLength);
            bool unknown = value == (0xFF >> sizeLength);
            for (int i = 1; i < sizeLength; i++) { value = (value << 8) | data[p + i]; unknown &= data[p + i] == 0xFF; }
            size = unknown ? -1 : value;
            dataStart = p + sizeLength;
            position = dataStart;
            return true;
        }

        private static int LeadingLength(byte first)
        {
            int length = 1;
            for (int mask = 0x80; mask != 0 && (first & mask) == 0; mask >>= 1) length++;
            return length;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try { client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false); }
                catch { return; }
                lock (_gate) _clients.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(Socket client)
        {
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 1024 * 1024;
                using var network = new NetworkStream(client, ownsSocket: false);
                FileStream? file = null;
                try
                {
                    // Several requests on one connection: the splitter reads the head, the index at the
                    // end and then the film, and re-opening the file each time is slow on a network mount.
                    while (!_stop.IsCancellationRequested)
                    {
                        string? request = await ReadRequestAsync(network).ConfigureAwait(false);
                        if (request == null) return;
                        string[] lines = request.Split("\r\n");
                        string[] first = lines[0].Split(' ');
                        bool head = first.Length > 0 && first[0] == "HEAD";
                        if (first.Length < 2 || (first[0] != "GET" && !head) || !first[1].StartsWith("/" + _token, StringComparison.Ordinal))
                        {
                            await WriteAsync(network, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                            return;
                        }
                        long from = 0, to = _length - 1;
                        bool ranged = false, keepAlive = false;
                        foreach (string line in lines.Skip(1))
                        {
                            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            {
                                string value = line[6..].Trim();
                                if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                                {
                                    string[] parts = value[6..].Split('-');
                                    if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long a))
                                    {
                                        from = a; ranged = true;
                                        if (long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long b)) to = Math.Min(b, _length - 1);
                                    }
                                }
                            }
                            else if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                                keepAlive = line.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
                        }
                        if (from < 0 || from >= _length || to < from)
                        {
                            await WriteAsync(network, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{_length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                            return;
                        }
                        long count = to - from + 1;
                        var response = new StringBuilder();
                        response.Append(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                        response.Append("Content-Type: video/x-matroska\r\nAccept-Ranges: bytes\r\n");
                        response.Append(CultureInfo.InvariantCulture, $"Content-Length: {count}\r\n");
                        if (ranged) response.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{to}/{_length}\r\n");
                        response.Append(keepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");
                        await WriteAsync(network, response.ToString()).ConfigureAwait(false);
                        if (!head)
                        {
                            file ??= new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous);
                            file.Position = from;
                            byte[] buffer = new byte[512 * 1024];
                            long position = from;
                            while (count > 0)
                            {
                                int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), _stop.Token).ConfigureAwait(false);
                                if (n <= 0) return;
                                foreach (long offset in _patchOffsets)
                                    if (offset >= position && offset < position + n) buffer[offset - position] = 1;
                                await network.WriteAsync(buffer.AsMemory(0, n), _stop.Token).ConfigureAwait(false);
                                position += n;
                                count -= n;
                            }
                        }
                        if (!keepAlive) return;
                    }
                }
                finally { file?.Dispose(); }
            }
            catch { /* the splitter drops the connection at every seek */ }
            finally
            {
                lock (_gate) _clients.Remove(client);
                try { client.Dispose(); } catch { }
            }
        }

        private async Task<string?> ReadRequestAsync(NetworkStream network)
        {
            var bytes = new List<byte>(1024);
            var one = new byte[1];
            while (bytes.Count < 16 * 1024)
            {
                int n = await network.ReadAsync(one.AsMemory(0, 1), _stop.Token).ConfigureAwait(false);
                if (n <= 0) return null;
                bytes.Add(one[0]);
                int c = bytes.Count;
                if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n')
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, c - 4);
            }
            return null;
        }

        private Task WriteAsync(NetworkStream network, string text) =>
            network.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).AsTask();

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            Socket[] clients;
            lock (_gate) { clients = _clients.ToArray(); _clients.Clear(); }
            foreach (var client in clients) { try { client.Dispose(); } catch { } }
        }
    }
}
