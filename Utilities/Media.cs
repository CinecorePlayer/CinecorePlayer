using System.Linq;
#nullable enable
using CinecorePlayer2025;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    // ======= Bootstrap FFmpeg: RootPath + avformat_network_init una volta =======
    internal static class FFmpegBootstrap
    {
        private static readonly object _lock = new();
        private static bool _initialized;

        public static void Ensure()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    var arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                    var candidate1 = Path.Combine(baseDir, "third-parties", "ffmpeg", arch);
                    var candidate2 = Path.Combine(baseDir, "ffmpeg", arch);
                    var candidate3 = Path.Combine(baseDir, "runtimes", arch, "native");

                    if (Directory.Exists(candidate1)) ffmpeg.RootPath = candidate1;
                    else if (Directory.Exists(candidate2)) ffmpeg.RootPath = candidate2;
                    else if (Directory.Exists(candidate3)) ffmpeg.RootPath = candidate3;

                    DynamicallyLoadedBindings.Initialize();
                    string ver = ffmpeg.av_version_info() ?? "?";

                    // Abilita protocolli di rete (http/https) per probe/sampler su streaming.
                    // Safe da chiamare più volte; utile soprattutto per URL YouTube (DASH).
                    try { ffmpeg.avformat_network_init(); } catch { }

                    _initialized = true;
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // ======= Helper unsafe comune =======
    internal static unsafe class FF
    {
        public static int OpenInputUtf8(string path, AVFormatContext** pFmt)
        {
            AVDictionary* opts = null;
            try
            {
                // Traccia di un CD audio: l'audio arriva dal lettore, su una porta locale (senza le opzioni per il web).
                if (AudioCd.IsStub(path))
                    return ffmpeg.avformat_open_input(pFmt, AudioCd.MapForPlayback(path), null, null);

                // Per gli URL https (YouTube/streaming) FFmpeg può ricevere 403 senza
                // User-Agent / Referer. Qui impostiamo header compatibili, senza
                // impattare i file locali.
                if (!string.IsNullOrWhiteSpace(path) &&
                    (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    ffmpeg.av_dict_set(&opts, "user_agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36", 0);

                    // YouTube (e alcune CDN) gradiscono un referer coerente
                    ffmpeg.av_dict_set(&opts, "referer", "https://www.youtube.com/", 0);

                    // Header addizionali
                    ffmpeg.av_dict_set(&opts, "headers",
                        "Accept-Language: it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7\r\n", 0);

                    // Timeout lettura (microsecondi)
                    ffmpeg.av_dict_set(&opts, "rw_timeout", "8000000", 0);

                    // Best-effort reconnect (ignorato se non supportato dal protocollo)
                    ffmpeg.av_dict_set(&opts, "reconnect", "1", 0);
                    ffmpeg.av_dict_set(&opts, "reconnect_streamed", "1", 0);
                    ffmpeg.av_dict_set(&opts, "reconnect_delay_max", "2", 0);
                }

                return ffmpeg.avformat_open_input(pFmt, path, null, &opts);
            }
            finally
            {
                if (opts != null)
                    ffmpeg.av_dict_free(&opts);
            }
        }
    }

    // ======= MediaProbe (FFmpeg) =======
    public static unsafe class MediaProbe
    {
        public static string? LastProbedPath { get; private set; }

        public sealed class AudioTags
        {
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string Album { get; set; } = string.Empty;
            public string AlbumArtist { get; set; } = string.Empty;
            public int? TrackNumber { get; set; }
            public int? TrackCount { get; set; }
            public double? DurationMinutes { get; set; }

            public bool HasAny =>
                !string.IsNullOrWhiteSpace(Title) ||
                !string.IsNullOrWhiteSpace(Artist) ||
                !string.IsNullOrWhiteSpace(Album) ||
                !string.IsNullOrWhiteSpace(AlbumArtist);
        }

        public sealed class Result
        {
            public double Duration;
            public bool HasVideo;
            public int Width, Height, VideoBits;
            public AVCodecID VideoCodec;
            public AVPixelFormat PixFmt;
            public AVColorPrimaries Primaries;
            public AVColorTransferCharacteristic Transfer;
            public double VideoFps;                // <-- FPS nominali
            public double SampleAspect = 1;        // SAR (pixel non quadrati, es. Full-SBS anamorfico 2:1)

            public AVCodecID AudioCodec;
            public int AudioRate, AudioChannels, AudioBits;
            public string AudioLayoutText = "";
            public bool AudioLooksObjectBased;
            public long AudioBitrate;
            public int AudioBitrateKbps;
            public string AudioStreamTitle = "";
            public string AudioCodecDisplayName = "";
            public bool IsHdr;
            /// <summary>Contenitore come lo riconosce FFmpeg (vale anche per i flussi di rete, che non hanno estensione).</summary>
            public string Format = "";
            /// <summary>Dimensione dichiarata dalla sorgente (file o server), 0 se sconosciuta.</summary>
            public long SizeBytes;
            public int OverallBitrateKbps;
            /// <summary>Profilo Dolby Vision dichiarato dal flusso video, 0 se assente.</summary>
            public int DolbyVisionProfile;
            public List<(string title, double start)> Chapters = new();
        }

        static MediaProbe() { FFmpegBootstrap.Ensure(); }

        public static AudioTags ReadAudioTags(string path)
        {
            var tags = new AudioTags();
            if (string.IsNullOrWhiteSpace(path))
                return tags;
            // Traccia di un CD audio: i dati sono gia' salvati, non serve far girare il disco.
            if (AudioCd.TryTags(path, out string cdTitle, out string cdArtist, out string cdAlbum, out int cdNumber, out int cdCount, out double cdSeconds))
            {
                tags.Title = cdTitle; tags.Artist = cdArtist; tags.Album = cdAlbum; tags.AlbumArtist = cdArtist;
                tags.TrackNumber = cdNumber; tags.TrackCount = cdCount; tags.DurationMinutes = cdSeconds / 60.0;
                return tags;
            }

            LastProbedPath = path;
            AVFormatContext* fmt = null;

            int openRc = FF.OpenInputUtf8(path, &fmt);
            if (openRc != 0 || fmt == null)
                return tags;

            try
            {
                // Album/artist/title tags are usually available immediately after
                // avformat_open_input (ID3, FLAC, MP4 containers). Probing every
                // stream for a tag-only library scan reopened hundreds of tracks
                // and could take tens of seconds on network storage.
                bool hasContainerIdentity =
                    !string.IsNullOrWhiteSpace(ReadDictTag(fmt->metadata, "title", "TITLE")) &&
                    !string.IsNullOrWhiteSpace(ReadDictTag(fmt->metadata, "album", "ALBUM")) &&
                    (!string.IsNullOrWhiteSpace(ReadDictTag(fmt->metadata, "artist", "ARTIST")) ||
                     !string.IsNullOrWhiteSpace(ReadDictTag(fmt->metadata, "album_artist", "ALBUMARTIST", "albumartist")));
                double fastDurationMinutes = 0;
                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var stream = fmt->streams[i];
                    if (stream != null && stream->duration > 0 && stream->duration != ffmpeg.AV_NOPTS_VALUE)
                        fastDurationMinutes = Math.Max(fastDurationMinutes,
                            stream->duration * ffmpeg.av_q2d(stream->time_base) / 60d);
                }
                if (!hasContainerIdentity || (fmt->duration <= 0 && fastDurationMinutes <= 0))
                    try { ffmpeg.avformat_find_stream_info(fmt, null); } catch { }

                tags.Title = ReadDictTag(fmt->metadata, "title", "TITLE") ?? string.Empty;
                tags.Artist = ReadDictTag(fmt->metadata, "artist", "ARTIST", "author", "AUTHOR", "composer", "COMPOSER") ?? string.Empty;
                tags.Album = ReadDictTag(fmt->metadata, "album", "ALBUM") ?? string.Empty;
                tags.AlbumArtist = ReadDictTag(fmt->metadata, "album_artist", "ALBUMARTIST", "albumartist", "AlbumArtist") ?? string.Empty;
                tags.TrackNumber = ParseTrackNumber(ReadDictTag(fmt->metadata, "track", "TRACK", "tracknumber", "TRACKNUMBER", "TRCK"));
                tags.TrackCount = ParseTrackNumber(ReadDictTag(fmt->metadata, "tracktotal", "totaltracks", "TRACKTOTAL", "TOTALTRACKS"))
                    ?? ParseTrackNumber((ReadDictTag(fmt->metadata, "track", "TRACK", "tracknumber", "TRCK") ?? "").Split('/').Skip(1).FirstOrDefault());
                if (fmt->duration > 0)
                    tags.DurationMinutes = fmt->duration / (double)ffmpeg.AV_TIME_BASE / 60d;
                else if (fastDurationMinutes > 0)
                    tags.DurationMinutes = fastDurationMinutes;

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var st = fmt->streams[i];
                    if (st == null || st->codecpar == null || st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                        continue;

                    if (string.IsNullOrWhiteSpace(tags.Title))
                        tags.Title = ReadDictTag(st->metadata, "title", "TITLE") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(tags.Artist))
                        tags.Artist = ReadDictTag(st->metadata, "artist", "ARTIST", "author", "AUTHOR") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(tags.Album))
                        tags.Album = ReadDictTag(st->metadata, "album", "ALBUM") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(tags.AlbumArtist))
                        tags.AlbumArtist = ReadDictTag(st->metadata, "album_artist", "ALBUMARTIST", "albumartist", "AlbumArtist") ?? string.Empty;
                    tags.TrackNumber ??= ParseTrackNumber(ReadDictTag(st->metadata, "track", "TRACK", "tracknumber", "TRACKNUMBER", "TRCK"));

                    if (tags.HasAny)
                        break;
                }
            }
            catch { }
            finally
            {
                if (fmt != null)
                {
                    var l = fmt;
                    ffmpeg.avformat_close_input(&l);
                }
            }

            tags.Title = CleanTag(tags.Title);
            tags.Artist = CleanTag(tags.Artist);
            tags.Album = CleanTag(tags.Album);
            tags.AlbumArtist = CleanTag(tags.AlbumArtist);
            return tags;
        }

        private static string CleanTag(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
        }

        private static int? ParseTrackNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string first = value.Split('/')[0].Trim();
            return int.TryParse(first, out int number) && number > 0 ? number : null;
        }

        private static string? ReadDictTag(AVDictionary* dict, params string[] keys)
        {
            if (dict == null || keys == null)
                return null;

            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var tag = ffmpeg.av_dict_get(dict, key, null, 0);
                if (tag == null || tag->value == null)
                    continue;

                string? value = Marshal.PtrToStringUTF8((nint)tag->value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // Molti tag (macOS, FLAC) sono in forma decomposta: "e" + accento combinante
                    // si confronta male con i servizi online e GDI lo disegna spezzato.
                    // ID3v1/Latin-1 frames scritti con byte UTF-8: stesso ripristino della cache.
                    return MusicTextIdentity.RepairEncoding(value);
                }
            }

            return null;
        }

        public static Result Probe(string path)
        {
            LastProbedPath = path;
            AVFormatContext* fmt = null;

            int openRc = FF.OpenInputUtf8(path, &fmt);
            if (openRc != 0)
                throw new ApplicationException($"Impossibile aprire il file (rc={openRc}).");

            try
            {
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new ApplicationException("Stream info non trovate.");

                var r = new Result
                {
                    Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0
                };

                int bestAudioScore = int.MinValue;
                int bestVideoScore = int.MinValue;
                sbyte* channelLayoutBuffer = stackalloc sbyte[128];

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var st = fmt->streams[i];
                    var par = st->codecpar;

                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        int score = ScoreVideoStream(st);
                        if (score == int.MinValue || score < bestVideoScore)
                            continue;

                        bestVideoScore = score;
                        r.HasVideo = true;
                        r.Width = par->width;
                        r.Height = par->height;
                        var sarQ = par->sample_aspect_ratio.num > 0 && par->sample_aspect_ratio.den > 0 ? par->sample_aspect_ratio : st->sample_aspect_ratio;
                        r.SampleAspect = sarQ.num > 0 && sarQ.den > 0 ? ffmpeg.av_q2d(sarQ) : 1;
                        r.VideoCodec = par->codec_id;
                        r.PixFmt = (AVPixelFormat)par->format;
                        r.VideoBits = GuessVideoBits(r.PixFmt, par->bits_per_raw_sample);
                        r.Primaries = par->color_primaries;
                        r.Transfer = par->color_trc;
                        r.VideoFps = GuessFps(st);
                        for (int side = 0; side < par->nb_coded_side_data; side++)
                        {
                            AVPacketSideData* data = &par->coded_side_data[side];
                            if (data->type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF && data->data != null && (long)data->size >= 8)
                                r.DolbyVisionProfile = data->data[2];
                        }
                    }
                    else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        int score = ScoreAudioStream(st);
                        if (score < bestAudioScore)
                            continue;

                        bestAudioScore = score;

                        r.AudioCodec = par->codec_id;
                        r.AudioRate = par->sample_rate;

                        int ch = par->ch_layout.nb_channels;
                        if (ch <= 0) ch = 2;
                        r.AudioChannels = ch;

                        r.AudioBits = GuessAudioBits(par->codec_id, par->format,
                                                     par->bits_per_coded_sample,
                                                     par->bits_per_raw_sample);

                        try
                        {
                            ffmpeg.av_channel_layout_describe(&par->ch_layout, (byte*)channelLayoutBuffer, 128);
                            r.AudioLayoutText = Marshal.PtrToStringAnsi((nint)channelLayoutBuffer) ?? "";
                        }
                        catch
                        {
                            r.AudioLayoutText = "";
                        }

                        long bitrate = par->bit_rate;
                        if (bitrate < 0) bitrate = 0;
                        r.AudioBitrate = bitrate;
                        r.AudioBitrateKbps = bitrate > 0 ? (int)Math.Round(bitrate / 1000.0) : 0;

                        string streamTitle = GetStreamTitle(st) ?? "";
                        r.AudioStreamTitle = streamTitle;
                        r.AudioLooksObjectBased = IsObjectBasedAudio(par->codec_id, streamTitle);
                        r.AudioCodecDisplayName = BuildAudioCodecDisplayName(par->codec_id, streamTitle);
                    }
                }

                // Capitoli
                for (int i = 0; i < fmt->nb_chapters; i++)
                {
                    var ch = fmt->chapters[i];
                    double tb = ffmpeg.av_q2d(ch->time_base);
                    double start = ch->start * tb;
                    string title = "Capitolo " + (i + 1);
                    var tag = ffmpeg.av_dict_get(ch->metadata, "title", null, 0);
                    if (tag != null) title = Marshal.PtrToStringUTF8((nint)tag->value) ?? title;
                    r.Chapters.Add((title, Math.Max(0, start)));
                }

                r.IsHdr = IsHdrLike(r.Transfer, r.Primaries, r.VideoBits);

                try
                {
                    string format = fmt->iformat != null && fmt->iformat->name != null ? Marshal.PtrToStringAnsi((nint)fmt->iformat->name) ?? "" : "";
                    r.Format = PrettyFormat(format);
                    r.OverallBitrateKbps = fmt->bit_rate > 0 ? (int)Math.Round(fmt->bit_rate / 1000.0) : 0;
                    if (fmt->pb != null)
                    {
                        long size = ffmpeg.avio_size(fmt->pb);
                        if (size > 0) r.SizeBytes = size;
                    }
                }
                catch { }

                return r;
            }
            finally
            {
                if (fmt != null)
                {
                    var l = fmt;
                    ffmpeg.avformat_close_input(&l);
                }
            }

            static string PrettyFormat(string name)
            {
                string n = name.ToLowerInvariant();
                return n.Length == 0 ? "" :
                    n.Contains("matroska") ? "Matroska" :
                    n.Contains("mp4") || n.Contains("mov") ? "MP4" :
                    n.Contains("mpegts") ? "MPEG-TS" :
                    n.Contains("hls") ? "HLS" :
                    n.Contains("dash") ? "DASH" :
                    n == "avi" ? "AVI" : n == "flv" ? "FLV" : n.Contains("asf") ? "ASF" : n == "ogg" ? "Ogg" :
                    n == "flac" ? "FLAC" : n == "mp3" ? "MP3" : n == "wav" ? "WAV" : n == "mpeg" ? "MPEG-PS" :
                    name.ToUpperInvariant();
            }

            static int GuessVideoBits(AVPixelFormat fmt, int bprs)
            {
                if (bprs > 0) return bprs;
                var d = ffmpeg.av_pix_fmt_desc_get(fmt);
                return d != null ? d->comp[0].depth : 8;
            }

            static int ScoreVideoStream(AVStream* st)
            {
                var par = st->codecpar;
                if (par == null || par->width <= 0 || par->height <= 0)
                    return int.MinValue;

                // Embedded cover art is exposed by FFmpeg as a video stream. It used to
                // overwrite the actual movie stream when it happened to be last, hence
                // labels such as "600x900 HDR" on a 4K title.
                try
                {
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                        return int.MinValue;
                }
                catch { }

                long pixels = (long)par->width * par->height;
                int score = (int)Math.Min(120_000_000L, pixels);
                try
                {
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0)
                        score += 150_000_000;
                }
                catch { }

                if (par->bit_rate > 0)
                    score += (int)Math.Min(20_000_000L, par->bit_rate / 4);

                // Still-image codecs are valid for photo files, but in a movie container
                // they are overwhelmingly likely to be cover/thumbnail streams.
                if (par->codec_id == AVCodecID.AV_CODEC_ID_MJPEG ||
                    par->codec_id == AVCodecID.AV_CODEC_ID_PNG ||
                    par->codec_id == AVCodecID.AV_CODEC_ID_BMP)
                {
                    score -= 100_000_000;
                }

                return score;
            }

            static int GuessAudioBits(AVCodecID id, int parFmt, int coded, int raw)
            {
                if (raw > 0) return raw;
                if (coded > 0) return coded;

                int fromCodec = ffmpeg.av_get_bits_per_sample(id);
                if (fromCodec > 0) return fromCodec;

                if (id == AVCodecID.AV_CODEC_ID_PCM_S16LE || id == AVCodecID.AV_CODEC_ID_PCM_S16BE) return 16;
                if (id == AVCodecID.AV_CODEC_ID_PCM_S24LE || id == AVCodecID.AV_CODEC_ID_PCM_S24BE) return 24;
                if (id == AVCodecID.AV_CODEC_ID_PCM_F32LE || id == AVCodecID.AV_CODEC_ID_PCM_F32BE) return 32;
                return 0;
            }

            static int ScoreAudioStream(AVStream* st)
            {
                var par = st->codecpar;
                int score = 0;

                try
                {
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0) score += 700;
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0) score -= 500;
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_HEARING_IMPAIRED) != 0) score -= 4_000;
                    if ((st->disposition & ffmpeg.AV_DISPOSITION_VISUAL_IMPAIRED) != 0) score -= 4_000;
                }
                catch { }

                int ch = par->ch_layout.nb_channels;
                if (ch <= 0) ch = 2;
                score += ch * 200;

                if (par->sample_rate > 0)
                    score += Math.Min(500, par->sample_rate / 100);

                if (par->bit_rate > 0)
                    score += Math.Min(5_000, (int)(par->bit_rate / 160));

                string title = (GetStreamTitle(st) ?? string.Empty).ToUpperInvariant();
                if (title.Contains("COMMENT")) score -= 8_000;
                if (title.Contains("DESCRIPTIVE") || title.Contains("DESCRITT")) score -= 6_000;
                if (title.Contains("HEARING") || title.Contains("COMMENTARY")) score -= 6_000;
                if (title.Contains("ATMOS") || title.Contains("JOC")) score += 6_000;
                if (title.Contains("DTS:X") || title.Contains("DTS X")) score += 6_000;
                if (title.Contains("DTS-HD MA") || title.Contains("MASTER AUDIO")) score += 4_500;
                if (title.Contains("DTS-HD HRA") || title.Contains("HIGH RES")) score += 3_200;

                score += par->codec_id switch
                {
                    AVCodecID.AV_CODEC_ID_TRUEHD => 12_000,
                    AVCodecID.AV_CODEC_ID_DTS => 7_000,
                    AVCodecID.AV_CODEC_ID_FLAC => 9_000,
                    AVCodecID.AV_CODEC_ID_EAC3 => 5_200,
                    AVCodecID.AV_CODEC_ID_AC3 => 2_500,
                    AVCodecID.AV_CODEC_ID_AAC => 1_500,
                    _ => 0
                };

                return score;
            }

            static string? GetStreamTitle(AVStream* st)
            {
                try
                {
                    string? ReadTag(string key)
                    {
                        var tag = ffmpeg.av_dict_get(st->metadata, key, null, 0);
                        return tag != null ? (Marshal.PtrToStringUTF8((nint)tag->value) ?? string.Empty) : null;
                    }

                    return ReadTag("title")
                        ?? ReadTag("handler_name")
                        ?? ReadTag("HANDLER_NAME")
                        ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            static bool IsObjectBasedAudio(AVCodecID codecId, string? streamTitle)
            {
                string s = (streamTitle ?? string.Empty).ToUpperInvariant();
                if (s.Contains("ATMOS") || s.Contains("JOC"))
                    return codecId == AVCodecID.AV_CODEC_ID_TRUEHD || codecId == AVCodecID.AV_CODEC_ID_EAC3;
                if (s.Contains("DTS:X") || s.Contains("DTS X"))
                    return codecId == AVCodecID.AV_CODEC_ID_DTS;
                return false;
            }

            static string BuildAudioCodecDisplayName(AVCodecID codecId, string? streamTitle)
            {
                string s = (streamTitle ?? string.Empty).ToUpperInvariant();

                if (codecId == AVCodecID.AV_CODEC_ID_TRUEHD)
                    return (s.Contains("ATMOS") || s.Contains("JOC")) ? "Dolby TrueHD Atmos" : "Dolby TrueHD";

                if (codecId == AVCodecID.AV_CODEC_ID_EAC3)
                    return (s.Contains("ATMOS") || s.Contains("JOC")) ? "Dolby Digital Plus Atmos" : "Dolby Digital Plus";

                if (codecId == AVCodecID.AV_CODEC_ID_AC3)
                    return "Dolby Digital";

                if (codecId == AVCodecID.AV_CODEC_ID_DTS)
                {
                    if (s.Contains("DTS:X") || s.Contains("DTS X")) return "DTS:X";
                    if (s.Contains("DTS-HD MA") || s.Contains("DTS HD MA") || s.Contains("MASTER AUDIO")) return "DTS-HD MA";
                    if (s.Contains("DTS-HD HRA") || s.Contains("DTS HD HRA") || s.Contains("HIGH RES")) return "DTS-HD HRA";
                    return "DTS";
                }

                if (codecId == AVCodecID.AV_CODEC_ID_FLAC) return "FLAC";
                if (codecId == AVCodecID.AV_CODEC_ID_AAC) return "AAC";
                if (codecId == AVCodecID.AV_CODEC_ID_OPUS) return "Opus";
                if (codecId == AVCodecID.AV_CODEC_ID_MP3) return "MP3";
                if (codecId.ToString().StartsWith("AV_CODEC_ID_PCM_", StringComparison.Ordinal)) return "PCM";
                return codecId.ToString().Replace("AV_CODEC_ID_", string.Empty);
            }

            static bool IsHdrLike(AVColorTransferCharacteristic trc, AVColorPrimaries prim, int bits)
            {
                bool pq = trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
                bool hlg = trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
                bool bt2020 = prim == AVColorPrimaries.AVCOL_PRI_BT2020;
                return pq || hlg || bt2020 && bits >= 10;
            }

            static double GuessFps(AVStream* st)
            {
                AVRational r = st->avg_frame_rate;
                double fps = 0;

                if (r.num > 0 && r.den > 0)
                {
                    fps = r.num / (double)r.den;
                }
                else
                {
                    r = st->r_frame_rate;
                    if (r.num > 0 && r.den > 0)
                        fps = r.num / (double)r.den;
                    else
                        fps = st->time_base.den != 0
                            ? 1.0 / ffmpeg.av_q2d(st->time_base)
                            : 0;
                }

                if (fps < 0.01 || fps > 500) return 0;
                return fps;
            }
        }

        public static bool IsPassthroughCandidate(AVCodecID id) =>
            id == AVCodecID.AV_CODEC_ID_TRUEHD || id == AVCodecID.AV_CODEC_ID_EAC3 ||
            id == AVCodecID.AV_CODEC_ID_AC3 || id == AVCodecID.AV_CODEC_ID_DTS;
    }

    // ======= Thumbnailer (FFmpeg) — thread-safe e idempotente =======
    internal sealed unsafe class Thumbnailer : IDisposable
    {
        private readonly object _lock = new();
        private AVFormatContext* _fmt;
        private int _vindex = -1;
        private AVCodecContext* _dec;
        private SwsContext* _sws;

        private int _lastSrcW, _lastSrcH;
        private AVPixelFormat _lastSrcFmt;
        private int _lastOutW, _lastOutH;
        private string? _srcPath;
        private bool _opened;
        private bool _disposed;

        public string? SourcePath => _srcPath;
        public event Action<string>? SourceOpened;

        public void Open(string path)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Thumbnailer));
                FFmpegBootstrap.Ensure();

                if (_opened && string.Equals(_srcPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Close_NoLock();

                AVFormatContext* f = null;
                int rcOpen = FF.OpenInputUtf8(path, &f);
                if (rcOpen != 0) throw new ApplicationException("Thumb open failed (rc=" + rcOpen + ")");

                if (ffmpeg.avformat_find_stream_info(f, null) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("Thumb si failed");
                }

                for (int i = 0; i < f->nb_streams; i++)
                    if (f->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _vindex = i;
                        break;
                    }

                if (_vindex < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("No video");
                }

                var par = f->streams[_vindex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("No decoder");
                }

                AVCodecContext* dec = ffmpeg.avcodec_alloc_context3(codec);
                if (ffmpeg.avcodec_parameters_to_context(dec, par) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("Ctx copy fail");
                }

                dec->thread_count = 2;
                dec->thread_type = ffmpeg.FF_THREAD_SLICE;
                if (ffmpeg.avcodec_open2(dec, codec, null) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    ffmpeg.avcodec_free_context(&dec);
                    throw new ApplicationException("Open dec fail");
                }

                _fmt = f;
                _dec = dec;
                _srcPath = path;
                _opened = true;
                SourceOpened?.Invoke(path);
                _lastSrcW = _lastSrcH = 0;
                _lastSrcFmt = (AVPixelFormat)(-1);
                _lastOutW = _lastOutH = 0;
            }
        }
        public Bitmap? Get(double seconds, int maxW = 360, CancellationToken ct = default, bool realtime = false, bool storyboard = false)
        {
            lock (_lock)
            {
                if (_disposed || !_opened || _fmt == null || _dec == null || _vindex < 0)
                    return null;

                if (ct.IsCancellationRequested)
                    return null;

                try
                {
                    // Open-GOP streams need their reference frames after a seek too.
                    // Skipping every non-key packet can return a damaged intra frame.
                    _dec->skip_frame = AVDiscard.AVDISCARD_DEFAULT;
                    var st = _fmt->streams[_vindex];
                    double tb = Math.Max(ffmpeg.av_q2d(st->time_base), 1e-12);
                    // Alcuni container hanno start_time != 0 sullo stream: riportiamo tutto a "secondi da inizio".
                    long startTs = (st->start_time != ffmpeg.AV_NOPTS_VALUE) ? st->start_time : 0;

                    // Keep the seek inside the stream without discarding the last
                    // 50 ms, which can contain several frames on high-fps video.
                    double maxSeconds = Math.Max(0.0, st->duration > 0 ? st->duration * tb : seconds);
                    if (maxSeconds > 0)
                        seconds = Math.Max(0, Math.Min(seconds, Math.Max(0, maxSeconds - 0.0005)));

                    long targetTs = startTs + (long)Math.Round(seconds / tb);
                    if (targetTs < startTs) targetTs = startTs;

                    // SEEK sullo stream video: in realtime privilegiamo un punto molto vicino
                    // al target; sul rilascio della timeline usiamo invece il percorso accurato.
                    // Always start from a keyframe. AVSEEK_FLAG_ANY frequently landed on
                    // an undecodable inter-frame and produced the uniform grey previews.
                    int seekFlags = ffmpeg.AVSEEK_FLAG_BACKWARD;
                    long seekWindowTs = (long)Math.Max(1, Math.Round((realtime ? 8.0 : 12.0) / tb));
                    long minSeekTs = Math.Max(startTs, targetTs - seekWindowTs);
                    int sk = ffmpeg.avformat_seek_file(_fmt, _vindex, minSeekTs, targetTs, targetTs, seekFlags);
                    if (sk < 0)
                        sk = ffmpeg.avformat_seek_file(_fmt, _vindex, long.MinValue, targetTs, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (sk < 0)
                        sk = ffmpeg.av_seek_frame(_fmt, _vindex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (sk < 0) return null;
                    ffmpeg.avcodec_flush_buffers(_dec);

                    AVPacket* pkt = ffmpeg.av_packet_alloc();
                    AVFrame* frame = ffmpeg.av_frame_alloc();
                    AVFrame* bestFrame = ffmpeg.av_frame_alloc();

                    try
                    {
                        // limiti adattivi: in modalità realtime (timeline preview) privilegiamo reattività
                        // (meno decode, tolleranza più larga, budget temporale).
                        double fps = ffmpeg.av_q2d(st->avg_frame_rate);
                        if (fps <= 0.1) fps = ffmpeg.av_q2d(st->r_frame_rate);
                        if (fps <= 0.1) fps = 25.0;

                        int maxFrames = realtime
                            ? (int)Math.Clamp(Math.Round(fps * 8.0), 120, 720)
                            : (int)Math.Clamp(Math.Round(fps * 12.0), 240, 1200);

                        int maxPackets = realtime ? 1800 : 4000;

                        long budgetTicks = (long)(Stopwatch.Frequency * (storyboard ? .65 : realtime ? .32 : .85));
                        long t0 = Stopwatch.GetTimestamp();

                        bool hasBest = false;
                        bool reachedTarget = false;
                        bool reachedEnd = false;
                        double bestFrameSec = double.NegativeInfinity;
                        int packetsRead = 0;
                        int framesRead = 0;
                        bool cancelled = false;
                        // After a seek the decoder may first emit open-GOP leading frames
                        // that reference the previous, never decoded GOP. FFmpeg conceals
                        // them without error flags, which produced smeared/grey previews.
                        // Nothing before the keyframe we seeked to is trusted; streams
                        // without keyframes (intra refresh) are trusted after ~1.5 s.
                        bool seenCleanStart = false;
                        int cleanStartFallback = (int)Math.Clamp(Math.Round(fps * 1.5), 24, 180);

                        while (packetsRead < maxPackets)
                        {
                            if (ffmpeg.av_read_frame(_fmt, pkt) < 0)
                            {
                                // The last decoded frames can still be buffered by a
                                // codec with B-frames. Drain it before treating EOF as
                                // the end of the timeline.
                                ffmpeg.avcodec_send_packet(_dec, null);
                                while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                                {
                                    if (ct.IsCancellationRequested) { cancelled = true; ffmpeg.av_frame_unref(frame); break; }
                                    if (Stopwatch.GetTimestamp() - t0 > budgetTicks) { ffmpeg.av_frame_unref(frame); break; }
                                    framesRead++;
                                    long tsFrame = frame->best_effort_timestamp;
                                    if (tsFrame == ffmpeg.AV_NOPTS_VALUE) tsFrame = frame->pts;
                                    if (frame->width > 0 && frame->height > 0 && frame->format >= 0 &&
                                        frame->data[0] != null && frame->decode_error_flags == 0 && (frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) == 0 && tsFrame != ffmpeg.AV_NOPTS_VALUE &&
                                        IsTrustedAfterSeek(frame, framesRead, cleanStartFallback, ref seenCleanStart))
                                    {
                                        if (storyboard)
                                        {
                                            ffmpeg.av_frame_unref(bestFrame);
                                            hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                            reachedTarget = hasBest;
                                            ffmpeg.av_frame_unref(frame);
                                            goto DoneDecoding;
                                        }
                                        double frameSec = (tsFrame - startTs) * tb;
                                        if (frameSec <= seconds + 0.0005 && frameSec >= bestFrameSec)
                                        {
                                            ffmpeg.av_frame_unref(bestFrame);
                                            hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                            if (hasBest) bestFrameSec = frameSec;
                                        }
                                        else if (frameSec > seconds + 0.0005)
                                        {
                                            reachedTarget = true;
                                            if (!hasBest && frameSec - seconds <= 1.0 / Math.Max(1.0, fps))
                                            {
                                                hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                                if (hasBest) bestFrameSec = frameSec;
                                            }
                                            ffmpeg.av_frame_unref(frame);
                                            break;
                                        }
                                    }
                                    ffmpeg.av_frame_unref(frame);
                                }
                                reachedEnd = !cancelled && Stopwatch.GetTimestamp() - t0 <= budgetTicks;
                                break;
                            }
                            if (ct.IsCancellationRequested) { cancelled = true; ffmpeg.av_packet_unref(pkt); break; }

                            if (Stopwatch.GetTimestamp() - t0 > budgetTicks)
                            {
                                ffmpeg.av_packet_unref(pkt);
                                goto DoneDecoding;
                            }

                            packetsRead++;
                            if (pkt->stream_index != _vindex)
                            {
                                ffmpeg.av_packet_unref(pkt);
                                continue;
                            }

                            if (ffmpeg.avcodec_send_packet(_dec, pkt) < 0)
                            {
                                ffmpeg.av_packet_unref(pkt);
                                continue;
                            }

                            ffmpeg.av_packet_unref(pkt);

                            while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                            {
                                if (ct.IsCancellationRequested) { cancelled = true; ffmpeg.av_frame_unref(frame); break; }

                                if (Stopwatch.GetTimestamp() - t0 > budgetTicks)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                framesRead++;
                                if (frame->width <= 0 || frame->height <= 0 || frame->format < 0 || frame->data[0] == null || frame->decode_error_flags != 0 || (frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    continue;
                                }

                                long tsFrame = frame->best_effort_timestamp;
                                if (tsFrame == ffmpeg.AV_NOPTS_VALUE)
                                    tsFrame = frame->pts;
                                if (tsFrame == ffmpeg.AV_NOPTS_VALUE ||
                                    !IsTrustedAfterSeek(frame, framesRead, cleanStartFallback, ref seenCleanStart))
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    continue;
                                }

                                if (storyboard)
                                {
                                    hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                    reachedTarget = hasBest;
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                // Convertiamo in seconds "a partire da 0" sottraendo startTs.
                                double frameSec = (tsFrame - startTs) * tb;
                                // A timeline time belongs to the latest presented frame at
                                // or before that time. A nearby keyframe is not equivalent.
                                if (frameSec <= seconds + 0.0005)
                                {
                                    if (frameSec >= bestFrameSec)
                                    {
                                        ffmpeg.av_frame_unref(bestFrame);
                                        hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                        if (hasBest) bestFrameSec = frameSec;
                                    }
                                    if (Math.Abs(frameSec - seconds) <= 0.0005)
                                    {
                                        reachedTarget = true;
                                        ffmpeg.av_frame_unref(frame);
                                        goto DoneDecoding;
                                    }
                                }
                                else
                                {
                                    reachedTarget = true;
                                    // Some streams begin after t=0 or their seek index lands
                                    // on the first frame after the requested time.
                                    if (!hasBest && frameSec - seconds <= 1.0 / Math.Max(1.0, fps))
                                    {
                                        hasBest = ffmpeg.av_frame_ref(bestFrame, frame) >= 0;
                                        if (hasBest) bestFrameSec = frameSec;
                                    }
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                if (framesRead >= maxFrames)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                ffmpeg.av_frame_unref(frame);
                            }

                            if (cancelled) break;
                        }

                    DoneDecoding:
                        if (ct.IsCancellationRequested || cancelled)
                            return null;

                        // A time budget may interrupt decoding before the requested frame.
                        // Never present the earlier keyframe as an exact preview.
                        if (!hasBest || (!reachedTarget &&
                            !(reachedEnd && seconds - bestFrameSec <= 1.0 / Math.Max(1.0, fps))))
                            return null;

                        var bmp = ToBitmap(bestFrame, maxW);
                        if (!LooksUsablePreview(bmp))
                        {
                            bmp.Dispose();
                            return null;
                        }
                        return bmp;
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&bestFrame);
                        ffmpeg.av_frame_free(&frame);
                        ffmpeg.av_packet_free(&pkt);
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool IsTrustedAfterSeek(AVFrame* frame, int framesRead, int fallbackFrames, ref bool seenCleanStart)
        {
            if (seenCleanStart) return true;
            if ((frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0 || frame->pict_type == AVPictureType.AV_PICTURE_TYPE_I || framesRead > fallbackFrames)
                seenCleanStart = true;
            return seenCleanStart;
        }

        private Bitmap ToBitmap(AVFrame* src, int maxW)
        {
            int srcW = Math.Max(1, src->width);
            int srcH = Math.Max(1, src->height);
            int dstW = Math.Min(Math.Max(1, maxW), srcW);
            double sar = src->sample_aspect_ratio.den > 0 && src->sample_aspect_ratio.num > 0
                ? ffmpeg.av_q2d(src->sample_aspect_ratio) : 1;
            int dstH = Math.Max(1, (int)Math.Round(srcH * dstW / (srcW * sar)));

            var curFmt = (AVPixelFormat)src->format;

            // Re-init SWS se cambia sorgente OPPURE la dimensione di uscita
            if (_sws == null ||
                _lastSrcW != srcW || _lastSrcH != srcH || _lastSrcFmt != curFmt ||
                _lastOutW != dstW || _lastOutH != dstH)
            {
                if (_sws != null)
                {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }

                _sws = ffmpeg.sws_getContext(
                    srcW, srcH, curFmt,
                    dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BICUBIC, null, null, null);

                _lastSrcW = srcW; _lastSrcH = srcH; _lastSrcFmt = curFmt;
                _lastOutW = dstW; _lastOutH = dstH;
            }

            if (_sws == null) throw new InvalidOperationException("Thumbnail scaler unavailable.");
            ApplySourceColorspace(src, srcW);
            var transfer = src->color_trc;
            bool hdr = transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ||
                       transfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
            bool wideGamut = src->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020;

            // Conversione diretta nel buffer del Bitmap
            var bmp = new Bitmap(dstW, dstH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, dstW, dstH);
            var lockd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);

            bool complete = false;
            try
            {
                byte_ptrArray4 dstPlanes = new();
                int_array4 dstLines = new();

                int stride = lockd.Stride;
                byte* basePtr = (byte*)lockd.Scan0;
                if (stride < 0)
                {
                    basePtr = (byte*)lockd.Scan0 + (long)-stride * (dstH - 1);
                    stride = -stride;
                }

                dstPlanes[0] = basePtr;
                dstLines[0] = stride;
                dstPlanes[1] = null; dstPlanes[2] = null; dstPlanes[3] = null;
                dstLines[1] = 0; dstLines[2] = 0; dstLines[3] = 0;

                complete = ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, srcH, dstPlanes, dstLines) == dstH;
                if (complete && (hdr || wideGamut))
                    MapToSdr(basePtr, stride, dstW, dstH, transfer, wideGamut);
            }
            finally
            {
                bmp.UnlockBits(lockd);
            }

            if (!complete) { bmp.Dispose(); throw new InvalidOperationException("Incomplete thumbnail conversion."); }
            return bmp;
        }

        // Senza dettagli espliciti swscale usa BT.601 limited: colori sbagliati su HD/UHD
        // e anteprime HDR grigie e slavate, scambiate per frame rotti.
        private void ApplySourceColorspace(AVFrame* src, int width)
        {
            int cs = src->colorspace switch
            {
                AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
                AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
                AVColorSpace.AVCOL_SPC_FCC => ffmpeg.SWS_CS_FCC,
                AVColorSpace.AVCOL_SPC_SMPTE240M => ffmpeg.SWS_CS_SMPTE240M,
                AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => ffmpeg.SWS_CS_ITU601,
                _ => src->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020 ? ffmpeg.SWS_CS_BT2020
                    : width >= 1280 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601
            };
            int srcRange = src->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            int* coefficients = ffmpeg.sws_getCoefficients(cs);
            if (coefficients == null) return;
            var table = new int_array4();
            for (int i = 0; i < 4; i++) table[(uint)i] = coefficients[i];
            ffmpeg.sws_setColorspaceDetails(_sws, table, srcRange, table, 1, 0, 1 << 16, 1 << 16);
        }

        private static float[]? _pqToSdr, _hlgToLinear;
        private static readonly byte[] _linearToSrgb = BuildLinearToSrgb();

        private static byte[] BuildLinearToSrgb()
        {
            var lut = new byte[4096];
            for (int i = 0; i < lut.Length; i++)
            {
                double l = i / (double)(lut.Length - 1);
                double v = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                lut[i] = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
            }
            return lut;
        }

        // PQ -> luminanza assoluta -> tone mapping BT.2390-like (bianco di riferimento 203 nit),
        // HLG -> OETF inversa; poi gamut BT.2020 -> BT.709 e codifica sRGB.
        private static float[] PqLut()
        {
            if (_pqToSdr != null) return _pqToSdr;
            const double m1 = 2610 / 16384.0, m2 = 2523 / 4096.0 * 128, c1 = 3424 / 4096.0, c2 = 2413 / 4096.0 * 32, c3 = 2392 / 4096.0 * 32;
            const double white = 4.0; // ~800 nit sul bianco di riferimento
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double e = Math.Pow(i / 255.0, 1 / m2);
                double nits = 10000 * Math.Pow(Math.Max(e - c1, 0) / (c2 - c3 * e), 1 / m1);
                double x = nits / 160.0;
                lut[i] = (float)(x * (1 + x / (white * white)) / (1 + x));
            }
            return _pqToSdr = lut;
        }

        private static float[] HlgLut()
        {
            if (_hlgToLinear != null) return _hlgToLinear;
            const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double e = i / 255.0;
                double l = e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
                lut[i] = (float)Math.Min(1, Math.Pow(l, 1.2) * 1.6);
            }
            return _hlgToLinear = lut;
        }

        private static float[]? _sdrToLinear;
        private static float[] SdrLut()
        {
            if (_sdrToLinear != null) return _sdrToLinear;
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = (float)Math.Pow(i / 255.0, 2.4);
            return _sdrToLinear = lut;
        }

        private static void MapToSdr(byte* basePtr, int stride, int width, int height, AVColorTransferCharacteristic transfer, bool wideGamut)
        {
            float[] toLinear = transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ? PqLut()
                : transfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 ? HlgLut() : SdrLut();
            byte[] encode = _linearToSrgb;
            int top = encode.Length - 1;
            for (int y = 0; y < height; y++)
            {
                byte* row = basePtr + (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte* p = row + x * 4;
                    float r = toLinear[p[2]], g = toLinear[p[1]], b = toLinear[p[0]];
                    if (wideGamut)
                    {
                        float r2 = 1.6605f * r - 0.5876f * g - 0.0728f * b;
                        float g2 = -0.1246f * r + 1.1329f * g - 0.0083f * b;
                        float b2 = -0.0182f * r - 0.1006f * g + 1.1187f * b;
                        r = r2; g = g2; b = b2;
                    }
                    p[2] = encode[(int)(Math.Clamp(r, 0f, 1f) * top)];
                    p[1] = encode[(int)(Math.Clamp(g, 0f, 1f) * top)];
                    p[0] = encode[(int)(Math.Clamp(b, 0f, 1f) * top)];
                }
            }
        }

        private static bool LooksUsablePreview(Bitmap bitmap)
        {
            try
            {
                double sum = 0;
                double sumSquared = 0;
                double channelDifference = 0;
                int count = 0;
                int stepX = Math.Max(1, bitmap.Width / 12);
                int stepY = Math.Max(1, bitmap.Height / 8);
                for (int y = stepY / 2; y < bitmap.Height; y += stepY)
                {
                    for (int x = stepX / 2; x < bitmap.Width; x += stepX)
                    {
                        Color c = bitmap.GetPixel(x, y);
                        double luma = c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722;
                        sum += luma;
                        sumSquared += luma * luma;
                        channelDifference += Math.Abs(c.R - c.G) + Math.Abs(c.G - c.B);
                        count++;
                    }
                }

                if (count == 0) return false;
                double mean = sum / count;
                double variance = Math.Max(0, sumSquared / count - mean * mean);
                bool uniformMidGrey = variance < 12 && mean >= 55 && mean <= 205 && channelDifference / count < 5;
                return !uniformMidGrey;
            }
            catch { return true; }
        }

        public void Close()
        {
            lock (_lock) Close_NoLock();
        }

        private void Close_NoLock()
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            if (_dec != null)
            {
                var d = _dec;
                ffmpeg.avcodec_free_context(&d);
                _dec = null;
            }
            if (_fmt != null)
            {
                var f = _fmt;
                ffmpeg.avformat_close_input(&f);
                _fmt = null;
            }

            _vindex = -1;
            _opened = false;
            _srcPath = null;
            _lastSrcW = _lastSrcH = 0;
            _lastSrcFmt = (AVPixelFormat)(-1);
            _lastOutW = _lastOutH = 0;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                Close_NoLock();
                _disposed = true;
            }
        }
    }

    // ======= PacketRateSampler (FFmpeg) — misura kbps reali in finestra breve =======
    internal sealed unsafe class PacketRateSampler : IDisposable
    {
        private readonly object _lock = new();
        private AVFormatContext* _fmt = null;
        private int _aIdx = -1, _vIdx = -1;
        private readonly List<int> _audioIndices = new();
        private readonly Dictionary<int, int> _metadataKbps = new();
        private bool _opened;

        public bool Open(string path)
        {
            Close();
            FFmpegBootstrap.Ensure();

            AVFormatContext* f = null;
            int rc = FF.OpenInputUtf8(path, &f);
            if (rc != 0) return false;
            if (ffmpeg.avformat_find_stream_info(f, null) < 0)
            {
                ffmpeg.avformat_close_input(&f);
                return false;
            }

            int ai = -1, vi = -1;
            var audio = new List<int>();
            var averages = new Dictionary<int, int>();
            for (int i = 0; i < (int)f->nb_streams; i++)
            {
                var st = f->streams[i];
                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audio.Add(i);
                    if (ai < 0) ai = i;
                }
                // Skip cover art: it is a "video" stream with a single attached picture.
                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && vi < 0 &&
                    (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                    vi = i;
                int kbps = MetadataAverageKbps(st);
                if (kbps > 0) averages[i] = kbps;
            }

            lock (_lock)
            {
                _fmt = f; _aIdx = ai; _vIdx = vi; _opened = true;
                _audioIndices.Clear(); _audioIndices.AddRange(audio);
                _metadataKbps.Clear();
                foreach (var pair in averages) _metadataKbps[pair.Key] = pair.Value;
            }

            return true;
        }

        /// <summary>
        /// Container-declared average of one stream: codec bit rate (MP4, TS), or the
        /// statistics tags mkvmerge writes (BPS, or NUMBER_OF_BYTES / DURATION).
        /// </summary>
        private static int MetadataAverageKbps(AVStream* st)
        {
            if (st->codecpar->bit_rate > 0)
                return (int)Math.Round(st->codecpar->bit_rate / 1000.0);

            string? Tag(string key)
            {
                AVDictionaryEntry* entry = ffmpeg.av_dict_get(st->metadata, key, null, 0);
                if (entry == null) entry = ffmpeg.av_dict_get(st->metadata, key + "-eng", null, 0);
                return entry == null ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)entry->value);
            }

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (long.TryParse(Tag("BPS"), System.Globalization.NumberStyles.Integer, invariant, out long bps) && bps > 0)
                return (int)Math.Round(bps / 1000.0);

            if (long.TryParse(Tag("NUMBER_OF_BYTES"), System.Globalization.NumberStyles.Integer, invariant, out long bytes) && bytes > 0 &&
                TimeSpan.TryParse(Tag("DURATION")?.Split('.')[0], invariant, out TimeSpan duration) && duration.TotalSeconds > 1)
                return (int)Math.Round(bytes * 8.0 / 1000.0 / duration.TotalSeconds);

            return 0;
        }

        /// <summary>Measures the n-th audio stream (the one actually playing).</summary>
        public void SelectAudioOrdinal(int ordinal)
        {
            lock (_lock)
            {
                if (ordinal >= 0 && ordinal < _audioIndices.Count)
                    _aIdx = _audioIndices[ordinal];
            }
        }

        /// <summary>Container-declared averages for the selected audio and the video stream; 0 when unknown.</summary>
        public (int AudioKbps, int VideoKbps) GetMetadataAverages()
        {
            lock (_lock)
            {
                int a = _aIdx >= 0 && _metadataKbps.TryGetValue(_aIdx, out int ak) ? ak : 0;
                int v = _vIdx >= 0 && _metadataKbps.TryGetValue(_vIdx, out int vk) ? vk : 0;
                return (a, v);
            }
        }

        /// <summary>
        /// Campiona byte audio/video in una finestra di ~windowSec a partire da tSec.
        /// Ritorna (audioKbps, videoKbps). Zero se non calcolabile.
        /// </summary>
        public (int aKbps, int vKbps) Sample(double tSec, double windowSec = 0.8)
        {
            lock (_lock)
            {
                if (!_opened || _fmt == null) return (0, 0);

                long ts = (long)(tSec * ffmpeg.AV_TIME_BASE);
                // seek "globale" più robusto
                int skl = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (skl < 0)
                    skl = ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, ts, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);

                // The seek lands on the keyframe before tSec. Counting from there put a
                // large intra frame at the start of almost every window and inflated the
                // "now" video rate; only packets presented from tSec onwards are counted.
                double startTime = _fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? _fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
                double from = tSec + startTime;

                long aBytes = 0, vBytes = 0;
                double t0A = double.MaxValue, t1A = double.MinValue, t0V = double.MaxValue, t1V = double.MinValue;

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                try
                {
                    int guardPkts = 0;
                    bool needA = _aIdx >= 0;
                    bool needV = _vIdx >= 0;
                    int guardMax = (needA && needV) ? 12000 : 4000;
                    while (ffmpeg.av_read_frame(_fmt, pkt) >= 0)
                    {
                        guardPkts++;
                        int si = pkt->stream_index;
                        if (si == _aIdx || si == _vIdx)
                        {
                            var st = _fmt->streams[si];
                            double tb = ffmpeg.av_q2d(st->time_base);
                            double pts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts * tb
                                       : pkt->dts != ffmpeg.AV_NOPTS_VALUE ? pkt->dts * tb : double.NaN;
                            double pdur = pkt->duration > 0 ? pkt->duration * tb : 0;

                            if (!double.IsNaN(pts) && pts + pdur > from)
                            {
                                // min/max, not first/last: B-frames arrive out of presentation order.
                                if (si == _aIdx)
                                {
                                    aBytes += pkt->size;
                                    t0A = Math.Min(t0A, pts); t1A = Math.Max(t1A, pts + pdur);
                                }
                                else
                                {
                                    vBytes += pkt->size;
                                    t0V = Math.Min(t0V, pts); t1V = Math.Max(t1V, pts + pdur);
                                }
                            }
                        }

                        ffmpeg.av_packet_unref(pkt);

                        // Condizione di stop: quando i flussi disponibili (audio/video) hanno coperto ~windowSec.
                        // IMPORTANTISSIMO per i DASH separati (YouTube): se manca audio o video,
                        // non dobbiamo aspettare anche l'altro.
                        double aDur = t1A > t0A ? t1A - t0A : 0;
                        double vDur = t1V > t0V ? t1V - t0V : 0;
                        bool aOk = !needA || aDur >= windowSec;
                        bool vOk = !needV || vDur >= windowSec;
                        if ((aOk && vOk) || guardPkts > guardMax)
                            break;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                }

                double aW = t1A > t0A ? t1A - t0A : 0;
                double vW = t1V > t0V ? t1V - t0V : 0;
                // A window shorter than a third of the request is noise, not a rate.
                int aK = aBytes > 0 && aW >= windowSec / 3 ? (int)Math.Round(aBytes * 8.0 / 1000.0 / aW) : 0;
                int vK = vBytes > 0 && vW >= windowSec / 3 ? (int)Math.Round(vBytes * 8.0 / 1000.0 / vW) : 0;

                return (aK, vK);
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_fmt != null)
                {
                    var f = _fmt;
                    ffmpeg.avformat_close_input(&f);
                }
                _fmt = null; _aIdx = _vIdx = -1; _opened = false;
                _audioIndices.Clear();
                _metadataKbps.Clear();
            }
        }

        public void Dispose() => Close();
    }
}
