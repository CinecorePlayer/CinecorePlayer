#nullable enable
using System;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using CinecorePlayer2025.Utilities;

namespace CinecorePlayer2025.Audio
{
    /// <summary>
    /// Decodifica con FFmpeg e converte in float interlacciato alla frequenza e al numero di
    /// canali dell'uscita. Il ricampionamento usa soxr ad alta precisione (quando serve):
    /// e' lo stesso ricampionatore dei player hi-fi, migliore di quello del mixer di Windows.
    /// Non e' thread-safe: lo usa un solo thread (quello del motore).
    /// </summary>
    internal sealed unsafe class CinecoreAudioDecoder : IDisposable
    {
        private AVFormatContext* _fmt;
        private AVCodecContext* _codec;
        private SwrContext* _swr;
        private AVPacket* _packet;
        private AVFrame* _frame;
        private int _stream;
        private AVRational _timeBase;
        private bool _draining, _flushedResampler;
        private double _skipUntilSeconds = -1;

        public int OutputRate { get; }
        public int OutputChannels { get; }
        public int SourceRate { get; private set; }
        public int SourceChannels { get; private set; }
        public string SourceFormat { get; private set; } = "";
        public bool SourceIsInteger { get; private set; }
        /// <summary>Bit significativi dei campioni interi della sorgente (16, 24 o 32).</summary>
        public int SourceBits { get; private set; }
        public double DurationSeconds { get; private set; }
        public bool EndOfStream { get; private set; }

        /// <summary>A fine file il ricampionatore non viene svuotato: i campioni che trattiene (il suo ritardo)
        /// restano li', cosi' il brano successivo puo' proseguire lo stesso filtro e attaccarsi senza salto.
        /// Chi lo imposta deve poi chiamare <see cref="FlushHeldResampler"/> o <see cref="TryContinueResamplerOf"/>.</summary>
        public bool HoldResamplerAtEnd { get; set; }

        /// <summary>Il file e' finito e il ricampionatore trattiene ancora la sua coda.</summary>
        public bool ResamplerHeld => EndOfStream && !_flushedResampler && _swr != null;

        /// <summary>Fa uscire la coda trattenuta (fine brano senza un seguito). Restituisce i fotogrammi scritti.</summary>
        public int FlushHeldResampler(float[] output)
        {
            if (_swr == null || _flushedResampler) return 0;
            _flushedResampler = true;
            return Convert(output, output.Length / OutputChannels, null, 0);
        }

        /// <summary>Prosegue il ricampionatore del brano precedente, se i due file hanno lo stesso formato:
        /// per il filtro e' un unico flusso continuo. Falso se i formati differiscono.</summary>
        public bool TryContinueResamplerOf(CinecoreAudioDecoder previous)
        {
            if (previous._swr == null || previous._codec == null || _codec == null || !previous.ResamplerHeld) return false;
            if (previous.OutputRate != OutputRate || previous.OutputChannels != OutputChannels) return false;
            if (previous._codec->sample_rate != _codec->sample_rate || previous._codec->sample_fmt != _codec->sample_fmt) return false;
            if (ffmpeg.av_channel_layout_compare(&previous._codec->ch_layout, &_codec->ch_layout) != 0) return false;
            if (_swr != null) { SwrContext* mine = _swr; ffmpeg.swr_free(&mine); }
            _swr = previous._swr;
            previous._swr = null;
            previous._flushedResampler = true;
            _flushedResampler = false;
            return true;
        }
        /// <summary>Guadagni ReplayGain dai tag (REPLAYGAIN_* o R128_* dell'Opus), in dB rispetto a -18 LUFS.</summary>
        public (double? Track, double? Album) ReplayGain { get; private set; }

        public CinecoreAudioDecoder(string path, int outputRate, int outputChannels, int streamOrdinal = -1)
        {
            OutputRate = outputRate;
            OutputChannels = outputChannels;
            FFmpegBootstrap.Ensure();
            try { Open(path, streamOrdinal); }
            catch { Dispose(); throw; }
        }

        private static string Err(int code)
        {
            const int size = 256;
            byte* buffer = stackalloc byte[size];
            ffmpeg.av_strerror(code, buffer, size);
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? code.ToString();
        }

        private static void Check(int result, string what)
        {
            if (result < 0) throw new InvalidOperationException($"{what}: {Err(result)}");
        }

        private void Open(string path, int streamOrdinal)
        {
            AVFormatContext* fmt = null;
            Check(FF.OpenInputUtf8(path, &fmt), "Apertura");
            _fmt = fmt;
            Check(ffmpeg.avformat_find_stream_info(_fmt, null), "Analisi dei flussi");

            _stream = -1;
            if (streamOrdinal >= 0)
            {
                int seen = 0;
                for (int i = 0; i < _fmt->nb_streams; i++)
                    if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && seen++ == streamOrdinal) { _stream = i; break; }
            }
            if (_stream < 0) _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Check(_stream, "Nessun flusso audio");

            AVStream* st = _fmt->streams[_stream];
            for (int i = 0; i < _fmt->nb_streams; i++)
                if (i != _stream) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL; // niente copertine/video da demuxare
            _timeBase = st->time_base;
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (decoder == null) throw new InvalidOperationException("Codec audio non supportato.");
            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            Check(ffmpeg.avcodec_parameters_to_context(_codec, st->codecpar), "Parametri del codec");
            _codec->pkt_timebase = st->time_base;
            _codec->request_sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
            Check(ffmpeg.avcodec_open2(_codec, decoder, null), "Apertura del codec");

            if (_codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                int n = _codec->ch_layout.nb_channels;
                ffmpeg.av_channel_layout_uninit(&_codec->ch_layout);
                ffmpeg.av_channel_layout_default(&_codec->ch_layout, Math.Max(1, n));
            }
            SourceRate = _codec->sample_rate;
            SourceChannels = _codec->ch_layout.nb_channels;
            int bits = st->codecpar->bits_per_raw_sample > 0 ? st->codecpar->bits_per_raw_sample : st->codecpar->bits_per_coded_sample;
            // Il decoder consegna interi (PCM, FLAC, ALAC, APE...) oppure virgola mobile (MP3, AAC, Opus...).
            SourceIsInteger = _codec->sample_fmt is AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P
                or AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P;
            // I bit veri sono quelli dichiarati dal file: un FLAC a 16 bit puo' uscire dal decoder
            // in un contenitore a 32 (allineato a sinistra) e resta comunque a 16 bit.
            SourceBits = bits is > 0 and <= 32 ? bits
                : _codec->sample_fmt is AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P ? 16 : 32;
            SourceFormat = $"{ffmpeg.avcodec_get_name(st->codecpar->codec_id)?.ToUpperInvariant()} · {SourceRate / 1000.0:0.#} kHz" + (bits > 0 ? $" · {bits} bit" : "");
            DurationSeconds = _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE
                : st->duration > 0 ? st->duration * ffmpeg.av_q2d(st->time_base) : 0;

            ReplayGain = ReadReplayGain(st);
            CreateResampler();
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }

        private (double? Track, double? Album) ReadReplayGain(AVStream* st)
        {
            double? track = null, album = null;
            void Scan(AVDictionary* dict)
            {
                AVDictionaryEntry* tag = null;
                while ((tag = ffmpeg.av_dict_get(dict, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
                {
                    string key = (Marshal.PtrToStringUTF8((IntPtr)tag->key) ?? "").ToUpperInvariant();
                    string value = Marshal.PtrToStringUTF8((IntPtr)tag->value) ?? "";
                    if (key is "REPLAYGAIN_TRACK_GAIN" or "R128_TRACK_GAIN" || key is "REPLAYGAIN_ALBUM_GAIN" or "R128_ALBUM_GAIN")
                    {
                        double? parsed = null;
                        if (key.StartsWith("R128", StringComparison.Ordinal))
                        {
                            // Opus: Q7.8 in LU rispetto a -23 LUFS; ReplayGain usa -18 (+5 dB).
                            if (int.TryParse(value.Trim(), out int q)) parsed = q / 256.0 + 5.0;
                        }
                        else
                        {
                            string number = new string(value.Trim().TakeWhile(c => char.IsDigit(c) || c is '-' or '+' or '.' or ',').ToArray()).Replace(',', '.');
                            if (double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double db)) parsed = db;
                        }
                        if (parsed is double v && Math.Abs(v) < 40)
                        {
                            if (key.Contains("TRACK")) track ??= v; else album ??= v;
                        }
                    }
                }
            }
            Scan(st->metadata);
            Scan(_fmt->metadata);
            return (track, album);
        }

        private void CreateResampler()
        {
            if (_swr != null) { SwrContext* old = _swr; ffmpeg.swr_free(&old); _swr = null; }
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, OutputChannels);
            SwrContext* swr = null;
            Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutputRate,
                &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate, 0, null), "Ricampionatore");
            if (_codec->sample_rate != OutputRate)
            {
                ffmpeg.av_opt_set(swr, "resampler", "soxr", 0);
                ffmpeg.av_opt_set_double(swr, "precision", 28, 0); // "very high quality"
            }
            // Downmix senza normalizzazione aggressiva: i canali restano coerenti col master.
            ffmpeg.av_opt_set_int(swr, "internal_sample_fmt", (long)AVSampleFormat.AV_SAMPLE_FMT_DBLP, 0);
            Check(ffmpeg.swr_init(swr), "Inizializzazione del ricampionatore");
            _swr = swr;
            ffmpeg.av_channel_layout_uninit(&outLayout);
            _flushedResampler = false;
        }

        /// <summary>
        /// Durata reale del flusso audio aperto, che puo' differire da quella del contenitore
        /// (tracce che finiscono prima dei titoli di coda o dei sottotitoli). Usa, nell'ordine,
        /// la durata del flusso, il tag DURATION dei file Matroska e l'ultimo pacchetto letto.
        /// </summary>
        public double StreamDurationSeconds()
        {
            AVStream* st = _fmt->streams[_stream];
            if (st->duration > 0 && st->duration != ffmpeg.AV_NOPTS_VALUE)
                return st->duration * ffmpeg.av_q2d(st->time_base);
            AVDictionaryEntry* tag = null;
            while ((tag = ffmpeg.av_dict_get(st->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                string key = Marshal.PtrToStringUTF8((IntPtr)tag->key) ?? "";
                if (!key.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)) continue;
                string value = Marshal.PtrToStringUTF8((IntPtr)tag->value) ?? "";
                if (value.Length > 16) value = value[..16]; // "01:59:39.7500000"
                if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var span)) return span.TotalSeconds;
            }
            // Ultimo pacchetto del flusso: seek verso la fine e lettura fino all'EOF.
            double end = 0;
            long target = (long)(Math.Max(0, DurationSeconds - 120) * ffmpeg.AV_TIME_BASE);
            if (ffmpeg.av_seek_frame(_fmt, -1, target, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
            {
                while (ffmpeg.av_read_frame(_fmt, _packet) >= 0)
                {
                    if (_packet->stream_index == _stream && _packet->pts != ffmpeg.AV_NOPTS_VALUE)
                        end = Math.Max(end, (_packet->pts + _packet->duration) * ffmpeg.av_q2d(st->time_base));
                    ffmpeg.av_packet_unref(_packet);
                }
                Seek(0);
            }
            return end > 0 ? end : DurationSeconds;
        }

        /// <summary>Posiziona la lettura; i campioni prima del punto vengono scartati (seek preciso).</summary>
        public void Seek(double seconds)
        {
            seconds = Math.Max(0, seconds);
            long ts = (long)(seconds * ffmpeg.AV_TIME_BASE);
            ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_codec);
            CreateResampler();
            _draining = false; EndOfStream = false;
            _skipUntilSeconds = seconds;
        }

        /// <summary>
        /// Riempie <paramref name="output"/> con fino a output.Length/OutputChannels fotogrammi.
        /// Restituisce i fotogrammi scritti; 0 con <see cref="EndOfStream"/> a fine brano.
        /// </summary>
        public int Read(float[] output, out double startSeconds)
        {
            startSeconds = double.NaN;
            int capacity = output.Length / OutputChannels;
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(_codec, _frame);
                if (ret >= 0)
                {
                    double frameStart = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                        ? _frame->best_effort_timestamp * ffmpeg.av_q2d(_timeBase) : double.NaN;
                    int written = Convert(output, capacity, (byte**)&_frame->data, _frame->nb_samples);
                    ffmpeg.av_frame_unref(_frame);
                    if (written <= 0) continue;
                    if (_skipUntilSeconds >= 0 && !double.IsNaN(frameStart))
                    {
                        double end = frameStart + written / (double)OutputRate;
                        if (end <= _skipUntilSeconds) continue;
                        int drop = (int)Math.Round((_skipUntilSeconds - frameStart) * OutputRate);
                        _skipUntilSeconds = -1;
                        if (drop > 0 && drop < written)
                        {
                            Array.Copy(output, drop * OutputChannels, output, 0, (written - drop) * OutputChannels);
                            written -= drop; frameStart += drop / (double)OutputRate;
                        }
                    }
                    startSeconds = frameStart;
                    return written;
                }
                if (ret == ffmpeg.AVERROR_EOF)
                {
                    if (!_flushedResampler && !HoldResamplerAtEnd)
                    {
                        _flushedResampler = true;
                        int tail = Convert(output, capacity, null, 0);
                        if (tail > 0) return tail;
                    }
                    EndOfStream = true;
                    return 0;
                }
                if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) throw new InvalidOperationException("Decodifica: " + Err(ret));

                if (_draining) { ffmpeg.avcodec_send_packet(_codec, null); continue; }
                int read = ffmpeg.av_read_frame(_fmt, _packet);
                if (read < 0)
                {
                    _draining = true;
                    ffmpeg.avcodec_send_packet(_codec, null);
                    continue;
                }
                if (_packet->stream_index == _stream)
                    ffmpeg.avcodec_send_packet(_codec, _packet); // i pacchetti corrotti vengono saltati
                ffmpeg.av_packet_unref(_packet);
            }
        }

        private int Convert(float[] output, int capacity, byte** input, int inputFrames)
        {
            fixed (float* dst = output)
            {
                byte* outPtr = (byte*)dst;
                int got = ffmpeg.swr_convert(_swr, &outPtr, capacity, input, inputFrames);
                return Math.Max(0, got);
            }
        }

        public void Dispose()
        {
            if (_frame != null) { AVFrame* f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet != null) { AVPacket* p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_swr != null) { SwrContext* s = _swr; ffmpeg.swr_free(&s); _swr = null; }
            if (_codec != null) { AVCodecContext* c = _codec; ffmpeg.avcodec_free_context(&c); _codec = null; }
            if (_fmt != null) { AVFormatContext* f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
        }
    }
}
