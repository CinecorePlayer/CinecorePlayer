#nullable enable
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Analisi HDR di un file: metadati dichiarati (schermo di mastering, MaxCLL/MaxFALL,
    /// Dolby Vision, HDR10+) e misura reale della luminosita' su fotogrammi campionati lungo
    /// tutto il film. Per ogni fotogramma: picco e media in nit (come li definisce CTA-861.3,
    /// sul massimo fra R, G e B), istogramma della luminosita' e quanta parte dell'immagine
    /// esce da Rec.709 e da DCI-P3.
    /// I fotogrammi sono i fotogrammi chiave piu' vicini ai punti scelti, letti con un
    /// decodificatore a parte: la riproduzione non viene toccata. I valori sono quindi una
    /// stima (un picco di pochi fotogrammi fra due campioni puo' sfuggire).
    /// </summary>
    internal static unsafe class HdrAnalyzer
    {
        public const int HistogramBins = 64;
        private const int CurrentVersion = 3;

        public sealed class Sample
        {
            public double Time { get; set; }
            /// <summary>Picco del fotogramma (99,99° percentile: un pixel isolato non conta).</summary>
            public float PeakNits { get; set; }
            /// <summary>Media del fotogramma (frame-average light level).</summary>
            public float AverageNits { get; set; }
        }

        /// <summary>Misura di un solo fotogramma (analisi in tempo reale).</summary>
        public sealed class FrameStats
        {
            /// <summary>Tempo del fotogramma misurato: il fotogramma chiave alla posizione richiesta o subito prima.</summary>
            public double Time { get; set; }
            public float PeakNits { get; set; }
            public float AverageNits { get; set; }
            /// <summary>Quota di pixel per intervallo di luminosita', come <see cref="Result.Histogram"/>.</summary>
            public double[] Histogram { get; set; } = new double[HistogramBins];
            public double ShareAbove100 { get; set; }
            public double ShareAbove400 { get; set; }
            public double ShareAbove1000 { get; set; }
            public double GamutRec709 { get; set; }
            public double GamutP3 { get; set; }
            public double GamutRec2020 { get; set; }
            public bool GamutMeasured { get; set; }
            /// <summary>Massimo assoluto del fotogramma (il picco e' il 99,99° percentile).</summary>
            public float MaxNits { get; set; }
            /// <summary>Luminanza media (Y); <see cref="AverageNits"/> e' la media del massimo fra R, G e B.</summary>
            public float LuminanceNits { get; set; }
            /// <summary>Livello del nero: 0,1° percentile.</summary>
            public float BlackNits { get; set; }
            public float MedianNits { get; set; }
            public float P90Nits { get; set; }
            public float P99Nits { get; set; }
            /// <summary>Immagine senza le bande nere, in pixel del video.</summary>
            public int ActiveWidth { get; set; }
            public int ActiveHeight { get; set; }
        }

        public sealed class Result
        {
            public int Version { get; set; }
            public int RequestedSamples { get; set; }
            public bool IsHdr { get; set; }
            /// <summary>"PQ", "HLG" oppure "SDR".</summary>
            public string Transfer { get; set; } = "SDR";
            public string Primaries { get; set; } = "";
            public int Width { get; set; }
            public int Height { get; set; }
            public int BitDepth { get; set; }
            public double Duration { get; set; }

            // Dichiarato nel file.
            public int? DeclaredMaxCll { get; set; }
            public int? DeclaredMaxFall { get; set; }
            public double? MasteringMaxNits { get; set; }
            public double? MasteringMinNits { get; set; }
            public int? DolbyVisionProfile { get; set; }
            public int? DolbyVisionCompatibility { get; set; }
            public bool Hdr10Plus { get; set; }
            /// <summary>Dolby Vision profilo 5: l'immagine di base non e' PQ/BT.2020 e i nit non si misurano.</summary>
            public bool BaseLayerNotMeasurable { get; set; }

            // Misurato.
            public List<Sample> Samples { get; set; } = new();
            public float MeasuredPeakNits { get; set; }
            public double MeasuredPeakTime { get; set; }
            public float MeasuredMaxAverageNits { get; set; }
            public float MedianPeakNits { get; set; }
            public float OverallAverageNits { get; set; }
            /// <summary>Quota di pixel per intervallo di luminosita'; gli intervalli sono uniformi sulla scala PQ.</summary>
            public double[] Histogram { get; set; } = new double[HistogramBins];
            public double ShareAbove100 { get; set; }
            public double ShareAbove400 { get; set; }
            public double ShareAbove1000 { get; set; }
            /// <summary>Quota di pixel (sopra 1 nit) dentro Rec.709, solo in P3, solo in BT.2020.</summary>
            public double GamutRec709 { get; set; }
            public double GamutP3 { get; set; }
            public double GamutRec2020 { get; set; }
            public bool GamutMeasured { get; set; }
            public float MeasuredMaxNits { get; set; }
            public double MeasuredMaxTime { get; set; }
            public float LuminanceAverageNits { get; set; }
            public float BlackNits { get; set; }
            public float MedianNits { get; set; }
            public float P90Nits { get; set; }
            public float P99Nits { get; set; }
            public int ActiveWidth { get; set; }
            public int ActiveHeight { get; set; }
            public string? Error { get; set; }

            /// <summary>Copia indipendente: l'analisi continua ad aggiungere campioni mentre l'interfaccia disegna.</summary>
            public Result Snapshot()
            {
                var copy = (Result)MemberwiseClone();
                copy.Samples = new List<Sample>(Samples);
                copy.Histogram = (double[])Histogram.Clone();
                return copy;
            }
        }

        // ===== Scala PQ (SMPTE ST 2084) =====
        private const double M1 = 2610 / 16384.0, M2 = 2523 / 4096.0 * 128, C1 = 3424 / 4096.0, C2 = 2413 / 4096.0 * 32, C3 = 2392 / 4096.0 * 32;

        /// <summary>Segnale PQ 0..1 -> nit.</summary>
        public static double PqToNits(double signal)
        {
            double e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
            return 10000 * Math.Pow(Math.Max(e - C1, 0) / (C2 - C3 * e), 1 / M1);
        }

        /// <summary>Nit -> segnale PQ 0..1 (l'asse dei grafici: uniforme per l'occhio).</summary>
        public static double NitsToPq(double nits)
        {
            double y = Math.Pow(Math.Clamp(nits, 0, 10000) / 10000, M1);
            return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
        }

        private const int LutSize = 4096;
        private static float[]? _pqNits, _hlgNits;

        private static float[] PqNitsLut()
        {
            if (_pqNits != null) return _pqNits;
            var lut = new float[LutSize];
            for (int i = 0; i < LutSize; i++) lut[i] = (float)PqToNits(i / (double)(LutSize - 1));
            return _pqNits = lut;
        }

        // HLG su uno schermo nominale da 1000 nit (gamma di sistema 1,2, applicata per canale).
        private static float[] HlgNitsLut()
        {
            if (_hlgNits != null) return _hlgNits;
            const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
            var lut = new float[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double e = i / (double)(LutSize - 1);
                double scene = e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
                lut[i] = (float)(1000 * Math.Pow(scene, 1.2));
            }
            return _hlgNits = lut;
        }

        // ===== Cache su disco =====
        private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CinecorePlayer2025", "hdr-analysis");

        private static string? CachePath(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                string identity = info.FullName.ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
                return Path.Combine(CacheFolder, hash + ".json");
            }
            catch { return null; }
        }

        /// <param name="minimumSamples">Un'analisi salvata con meno campioni di quelli richiesti non basta.</param>
        public static Result? TryLoadCached(string path, int minimumSamples)
        {
            try
            {
                string? file = CachePath(path);
                if (file == null || !File.Exists(file)) return null;
                var result = JsonSerializer.Deserialize<Result>(File.ReadAllText(file));
                return result != null && result.Version == CurrentVersion && result.Error == null &&
                       (!result.IsHdr || result.RequestedSamples >= minimumSamples) ? result : null;
            }
            catch { return null; }
        }

        private static void SaveCache(string path, Result result)
        {
            try
            {
                string? file = CachePath(path);
                if (file == null) return;
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(file, JsonSerializer.Serialize(result));
            }
            catch { }
        }

        /// <summary>Solo cio' che il file dichiara (formato, dimensioni, durata, metadati HDR):
        /// nessun fotogramma decodificato, qualche decina di millisecondi per file.</summary>
        public static Result ReadDeclared(string path)
        {
            FFmpegBootstrap.Ensure();
            var result = new Result { Version = CurrentVersion };
            AVFormatContext* fmt = null;
            AVCodecContext* dec = null;
            AVPacket* pkt = null;
            AVFrame* frame = null;
            try
            {
                if (FF.OpenInputUtf8(path, &fmt) != 0 || fmt == null) { result.Error = "open"; return result; }
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { result.Error = "streams"; return result; }
                int videoIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (videoIndex < 0) { result.Error = "no-video"; return result; }
                AVCodecParameters* par = fmt->streams[videoIndex]->codecpar;
                result.Width = par->width;
                result.Height = par->height;
                result.Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                result.Transfer = par->color_trc switch
                {
                    AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => "PQ",
                    AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => "HLG",
                    _ => "SDR"
                };
                result.IsHdr = result.Transfer != "SDR";
                ReadStreamSideData(par, result);

                // Molti file portano MaxCLL e schermo di mastering solo dentro il flusso video
                // (messaggi SEI), non nel contenitore: per leggerli serve il primo fotogramma.
                if (result.IsHdr && (result.DeclaredMaxCll == null || result.MasteringMaxNits == null))
                {
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    if (codec != null)
                    {
                        dec = ffmpeg.avcodec_alloc_context3(codec);
                        if (ffmpeg.avcodec_parameters_to_context(dec, par) >= 0 && ffmpeg.avcodec_open2(dec, codec, null) >= 0)
                        {
                            pkt = ffmpeg.av_packet_alloc();
                            frame = ffmpeg.av_frame_alloc();
                            for (int read = 0; read < 400; read++)
                            {
                                if (ffmpeg.av_read_frame(fmt, pkt) < 0) break;
                                bool key = pkt->stream_index == videoIndex && (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                                if (key && ffmpeg.avcodec_send_packet(dec, pkt) >= 0)
                                {
                                    ffmpeg.av_packet_unref(pkt);
                                    ffmpeg.avcodec_send_packet(dec, null);
                                    if (ffmpeg.avcodec_receive_frame(dec, frame) >= 0)
                                    {
                                        ReadFrameSideData(frame, result);
                                        if (ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null)
                                            result.Hdr10Plus = true;
                                    }
                                    break;
                                }
                                ffmpeg.av_packet_unref(pkt);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { result.Error = ex.GetType().Name; }
            finally
            {
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (dec != null) ffmpeg.avcodec_free_context(&dec);
                if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            }
            return result;
        }

        /// <summary>Analizza il file. Va chiamato fuori dal thread dell'interfaccia.</summary>
        /// <param name="progress">Avanzamento 0..1 e risultato parziale (per disegnare il grafico mentre cresce).</param>
        public static Result Analyze(string path, int sampleCount, Action<double, Result>? progress, CancellationToken ct)
        {
            FFmpegBootstrap.Ensure();
            var result = new Result { Version = CurrentVersion };
            AVFormatContext* fmt = null;
            AVCodecContext* dec = null;
            FrameMeter? meter = null;
            AVPacket* pkt = null;
            AVFrame* frame = null;
            try
            {
                if (!OpenVideo(path, result, &fmt, out int videoIndex)) return result;
                AVStream* stream = fmt->streams[videoIndex];
                if (!result.IsHdr) return result;

                dec = OpenDecoder(stream->codecpar);
                if (dec == null) { result.Error = "decoder"; return result; }

                pkt = ffmpeg.av_packet_alloc();
                frame = ffmpeg.av_frame_alloc();

                double timeBase = Math.Max(ffmpeg.av_q2d(stream->time_base), 1e-12);
                long startTs = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
                double duration = result.Duration > 0 ? result.Duration : stream->duration > 0 ? stream->duration * timeBase : 0;
                if (duration <= 0) { result.Error = "duration"; return result; }
                result.Duration = duration;

                sampleCount = Math.Clamp(sampleCount, 8, 2000);
                result.RequestedSamples = sampleCount;
                bool measureGamut = result.Primaries == "BT.2020";
                meter = new FrameMeter(result.Transfer == "HLG", result.Primaries);

                var totals = new Totals();
                long lastFramePts = long.MinValue;

                for (int index = 0; index < sampleCount; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    double target = (index + 0.5) * duration / sampleCount;
                    long targetTs = startTs + (long)Math.Round(target / timeBase);
                    if (ffmpeg.avformat_seek_file(fmt, videoIndex, long.MinValue, targetTs, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0 &&
                        ffmpeg.av_seek_frame(fmt, videoIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
                        continue;
                    ffmpeg.avcodec_flush_buffers(dec);

                    // Primo fotogramma chiave dopo il salto: inviato da solo e svuotato subito,
                    // senza decodificare i fotogrammi che lo seguono.
                    bool gotFrame = false;
                    for (int read = 0; read < 600 && !gotFrame; read++)
                    {
                        if (ffmpeg.av_read_frame(fmt, pkt) < 0) break;
                        bool video = pkt->stream_index == videoIndex;
                        bool key = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                        if (video && key && ffmpeg.avcodec_send_packet(dec, pkt) >= 0)
                        {
                            ffmpeg.av_packet_unref(pkt);
                            ffmpeg.avcodec_send_packet(dec, null);
                            gotFrame = ffmpeg.avcodec_receive_frame(dec, frame) >= 0;
                            break;
                        }
                        ffmpeg.av_packet_unref(pkt);
                    }
                    if (!gotFrame || frame->width <= 0 || frame->height <= 0 || frame->data[0] == null)
                    {
                        ffmpeg.av_frame_unref(frame);
                        continue;
                    }

                    long framePts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                    if (framePts == lastFramePts) { ffmpeg.av_frame_unref(frame); continue; } // stesso fotogramma chiave del campione prima
                    lastFramePts = framePts;
                    if (ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null)
                        result.Hdr10Plus = true;
                    ReadFrameSideData(frame, result);

                    int measured = meter.Measure(frame);
                    ffmpeg.av_frame_unref(frame);
                    if (measured < 0) { result.Error = "scaler"; return result; }
                    if (measured == 0) continue;

                    totals.Add(meter);
                    result.ActiveWidth = meter.ActiveWidth; result.ActiveHeight = meter.ActiveHeight;

                    float peak = meter.PeakNits, average = meter.AverageNits;
                    double time = Math.Max(0, (framePts - startTs) * timeBase);
                    result.Samples.Add(new Sample { Time = time, PeakNits = peak, AverageNits = average });
                    if (meter.MaxNits > result.MeasuredMaxNits) { result.MeasuredMaxNits = meter.MaxNits; result.MeasuredMaxTime = time; }
                    if (peak > result.MeasuredPeakNits) { result.MeasuredPeakNits = peak; result.MeasuredPeakTime = time; }
                    if (average > result.MeasuredMaxAverageNits) result.MeasuredMaxAverageNits = average;

                    if (progress != null && (index % 4 == 3 || index == sampleCount - 1))
                    {
                        Summarize(result, totals, measureGamut);
                        progress((index + 1) / (double)sampleCount, result.Snapshot());
                    }
                }

                Summarize(result, totals, measureGamut);
                if (result.Samples.Count == 0) result.Error = "no-frames";
                else SaveCache(path, result);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name;
                return result;
            }
            finally
            {
                meter?.Dispose();
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (pkt != null) ffmpeg.av_packet_free(&pkt);
                if (dec != null) ffmpeg.avcodec_free_context(&dec);
                if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            }
        }

        /// <summary>Apre il file e descrive il flusso video in <paramref name="result"/>.
        /// False (con <see cref="Result.Error"/>) se il file non si puo' leggere.</summary>
        private static bool OpenVideo(string path, Result result, AVFormatContext** fmt, out int videoIndex)
        {
            videoIndex = -1;
            if (FF.OpenInputUtf8(path, fmt) != 0 || *fmt == null) { result.Error = "open"; return false; }
            if (ffmpeg.avformat_find_stream_info(*fmt, null) < 0) { result.Error = "streams"; return false; }

            videoIndex = ffmpeg.av_find_best_stream(*fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIndex < 0) { result.Error = "no-video"; return false; }
            AVCodecParameters* par = (*fmt)->streams[videoIndex]->codecpar;

            result.Width = par->width;
            result.Height = par->height;
            result.Duration = (*fmt)->duration > 0 ? (*fmt)->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            var descriptor = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)par->format);
            result.BitDepth = par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample : descriptor != null ? descriptor->comp[0].depth : 8;
            result.Transfer = par->color_trc switch
            {
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => "PQ",
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => "HLG",
                _ => "SDR"
            };
            result.Primaries = par->color_primaries switch
            {
                AVColorPrimaries.AVCOL_PRI_BT2020 => "BT.2020",
                AVColorPrimaries.AVCOL_PRI_BT709 => "Rec.709",
                AVColorPrimaries.AVCOL_PRI_SMPTE432 => "P3-D65",
                AVColorPrimaries.AVCOL_PRI_SMPTE431 => "DCI-P3",
                _ => ""
            };
            result.IsHdr = result.Transfer != "SDR";
            ReadStreamSideData(par, result);
            // Dolby Vision profilo 5: nessuna base HDR10, i colori dello strato base sono IPT.
            result.BaseLayerNotMeasurable = result.DolbyVisionProfile == 5;
            return true;
        }

        /// <param name="frameThreads">Piu' fotogrammi in parallelo: serve a decodificare il film di
        /// seguito in tempo reale; per i fotogrammi chiave isolati bastano le slice.</param>
        private static AVCodecContext* OpenDecoder(AVCodecParameters* par, bool frameThreads = false)
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (codec == null) return null;
            AVCodecContext* dec = ffmpeg.avcodec_alloc_context3(codec);
            if (ffmpeg.avcodec_parameters_to_context(dec, par) >= 0)
            {
                dec->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
                dec->thread_type = frameThreads ? ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE : ffmpeg.FF_THREAD_SLICE;
                if (ffmpeg.avcodec_open2(dec, codec, null) >= 0) return dec;
            }
            ffmpeg.avcodec_free_context(&dec);
            return null;
        }

        /// <summary>
        /// Strumenti di un fotogramma (solo in tempo reale): forma d'onda dei tre canali,
        /// cromaticita' CIE 1931, vettorscopio e immagine in falsi colori. I contatori sono
        /// grezzi: chi disegna decide guadagno e colori.
        /// </summary>
        public sealed class ScopeData
        {
            public const int WaveColumns = 256, WaveLevels = 256, ChromaSize = 160, VectorSize = 160;
            /// <summary>Estensione del diagramma di cromaticita' (x da 0 a 0,8; y da 0 a 0,9).</summary>
            public const double ChromaMaxX = 0.80, ChromaMaxY = 0.90;
            /// <summary>Cb e Cr al bordo del vettorscopio (i colori HDR in PQ stanno vicino al centro).</summary>
            public const float VectorRange = 0.15f;

            /// <summary>Colonna * WaveLevels + livello; il livello e' la posizione sull'asse PQ (0..10.000 nit).</summary>
            public readonly int[] WaveR = new int[WaveColumns * WaveLevels], WaveG = new int[WaveColumns * WaveLevels], WaveB = new int[WaveColumns * WaveLevels];
            /// <summary>Riga (y) * ChromaSize + colonna (x).</summary>
            public readonly int[] Chroma = new int[ChromaSize * ChromaSize];
            /// <summary>Riga (Cr, dall'alto) * VectorSize + colonna (Cb).</summary>
            public readonly int[] Vector = new int[VectorSize * VectorSize];
            /// <summary>Immagine ridotta in falsi colori, BGRA.</summary>
            public byte[] FalseColor = Array.Empty<byte>();
            public int PictureWidth, PictureHeight, SamplesPerColumn = 1, ChromaPixels, Pixels;

            internal void Prepare(int pictureWidth, int pictureHeight)
            {
                Array.Clear(WaveR); Array.Clear(WaveG); Array.Clear(WaveB); Array.Clear(Chroma); Array.Clear(Vector);
                if (PictureWidth != pictureWidth || PictureHeight != pictureHeight || FalseColor.Length != pictureWidth * pictureHeight * 4)
                {
                    PictureWidth = pictureWidth; PictureHeight = pictureHeight;
                    FalseColor = new byte[pictureWidth * pictureHeight * 4];
                }
            }
        }

        /// <summary>Fasce dei falsi colori: da quanti nit parte ciascuna e il suo colore (ARGB).</summary>
        public static readonly (double FromNits, uint Argb)[] FalseColorBands =
        {
            (0, 0xFF161A26), (0.5, 0xFF2B3A8C), (5, 0xFF1F7FA6), (25, 0xFF2E9E62), (100, 0xFF9DB53A),
            (203, 0xFFE6C63A), (400, 0xFFEC8B2E), (1000, 0xFFE0453A), (2000, 0xFFE44FBF), (4000, 0xFFF4F4F4)
        };

        private static uint[]? _falseColor;

        // Colore per ogni valore dell'asse PQ a 10 bit: quello della fascia, piu' scuro all'inizio
        // della fascia e pieno alla fine, cosi' dentro una fascia si legge ancora l'immagine.
        private static uint[] FalseColorLut()
        {
            if (_falseColor != null) return _falseColor;
            var lut = new uint[1024];
            for (int code = 0; code < 1024; code++)
            {
                double nits = PqToNits(code / 1023.0);
                int band = 0;
                while (band + 1 < FalseColorBands.Length && nits >= FalseColorBands[band + 1].FromNits) band++;
                double from = NitsToPq(FalseColorBands[band].FromNits), to = band + 1 < FalseColorBands.Length ? NitsToPq(FalseColorBands[band + 1].FromNits) : 1;
                double shade = 0.62 + 0.38 * Math.Clamp((code / 1023.0 - from) / Math.Max(1e-6, to - from), 0, 1);
                if (band == 0) shade = 0.25 + 0.75 * Math.Clamp((code / 1023.0 - from) / Math.Max(1e-6, to - from), 0, 1);
                uint argb = FalseColorBands[band].Argb;
                uint r = (uint)(((argb >> 16) & 255) * shade), g = (uint)(((argb >> 8) & 255) * shade), b = (uint)((argb & 255) * shade);
                lut[code] = 0xFF000000 | (r << 16) | (g << 8) | b;
            }
            return _falseColor = lut;
        }

        /// <summary>
        /// Misura di un fotogramma decodificato: l'immagine viene ridotta per campionamento
        /// (pixel veri, nessuna media) in R'G'B' a 16 bit, poi se ne ricavano picco, massimo,
        /// media, percentili, istogramma e gamut, sulla sola area attiva (senza le bande nere,
        /// che abbasserebbero media e livello del nero). Con <see cref="ScopeData"/> riempie
        /// anche gli strumenti. La usano sia l'analisi dell'intero film sia quella in tempo reale.
        /// </summary>
        private sealed class FrameMeter : IDisposable
        {
            private readonly float[] _nitsLut;
            // Posizione sull'asse PQ di ogni valore della tabella (per l'istogramma e per i percentili).
            private readonly byte[] _binOfLut = new byte[LutSize];
            private readonly short[] _codeOfLut = new short[LutSize];
            private readonly bool _measureGamut;
            // Da RGB lineare (nei primari del file) a CIE XYZ; la riga di mezzo e' la luminanza.
            private readonly float _xr, _xg, _xb, _yr, _yg, _yb, _zr, _zg, _zb;
            private readonly int _barIndex;
            private SwsContext* _sws;
            private IntPtr _planeMemory;
            private int _outW, _outH, _srcW, _srcH, _planeStride, _step = 1;
            private AVPixelFormat _srcFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            private ushort* _planeG, _planeB, _planeR;
            private float _kr = 0.2627f, _kb = 0.0593f;
            // Area attiva: unione di tutto cio' che si e' visto acceso finora (le bande nere di
            // un film non cambiano; una scena buia non deve restringerla).
            private int _top = int.MaxValue, _bottom = -1, _left = int.MaxValue, _right = -1;
            private int[] _columnOf = Array.Empty<int>();
            private int _columnWidth = -1;

            // Ultimo fotogramma misurato.
            public readonly int[] Bins = new int[HistogramBins];
            public readonly int[] Codes = new int[1024];
            public int Count;
            public long Above100, Above400, Above1000, GamutPixels, In709, InP3;
            public float PeakNits, AverageNits, MaxNits, LuminanceNits, BlackNits, MedianNits, P90Nits, P99Nits;
            public int ActiveWidth, ActiveHeight;

            public FrameMeter(bool hlg, string primaries)
            {
                _nitsLut = hlg ? HlgNitsLut() : PqNitsLut();
                _measureGamut = primaries == "BT.2020";
                float[] m = primaries switch
                {
                    "Rec.709" => new[] { 0.412391f, 0.357584f, 0.180481f, 0.212639f, 0.715169f, 0.072192f, 0.019331f, 0.119195f, 0.950532f },
                    "P3-D65" or "DCI-P3" => new[] { 0.486571f, 0.265668f, 0.198217f, 0.228975f, 0.691739f, 0.079287f, 0f, 0.045113f, 1.043944f },
                    _ => new[] { 0.636958f, 0.144617f, 0.168881f, 0.262700f, 0.677998f, 0.059302f, 0f, 0.028073f, 1.060985f }
                };
                _xr = m[0]; _xg = m[1]; _xb = m[2]; _yr = m[3]; _yg = m[4]; _yb = m[5]; _zr = m[6]; _zg = m[7]; _zb = m[8];
                _barIndex = LutSize - 1;
                for (int i = 0; i < LutSize; i++)
                {
                    _binOfLut[i] = (byte)Math.Min(HistogramBins - 1, (int)(NitsToPq(_nitsLut[i]) * HistogramBins));
                    _codeOfLut[i] = (short)Math.Min(1023, (int)(NitsToPq(_nitsLut[i]) * 1023));
                    // Sotto 0,3 nit una riga e' banda nera (il rumore di compressione resta ben sotto).
                    if (_barIndex == LutSize - 1 && _nitsLut[i] > 0.3f) _barIndex = i;
                }
            }

            private ushort* Row(ushort* plane, int y) => (ushort*)((byte*)plane + (long)y * _planeStride);

            private bool RowLit(int y, int from, int to)
            {
                ushort* g = Row(_planeG, y), b = Row(_planeB, y), r = Row(_planeR, y);
                int bar = _barIndex;
                for (int x = from; x <= to; x++)
                    if ((r[x] >> 4) >= bar || (g[x] >> 4) >= bar || (b[x] >> 4) >= bar) return true;
                return false;
            }

            private bool ColumnLit(int x, int from, int to)
            {
                int bar = _barIndex;
                for (int y = from; y <= to; y++)
                    if ((Row(_planeR, y)[x] >> 4) >= bar || (Row(_planeG, y)[x] >> 4) >= bar || (Row(_planeB, y)[x] >> 4) >= bar) return true;
                return false;
            }

            /// <returns>1 misurato, 0 fotogramma non convertibile (si salta), -1 convertitore non disponibile.</returns>
            public int Measure(AVFrame* frame, ScopeData? scopes = null)
            {
                var format = (AVPixelFormat)frame->format;
                if (_sws == null || _srcW != frame->width || _srcH != frame->height || _srcFormat != format)
                {
                    if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
                    _srcW = frame->width; _srcH = frame->height; _srcFormat = format;
                    _step = Math.Max(1, (int)Math.Round(_srcW / 960.0));
                    _outW = Math.Max(16, _srcW / _step);
                    _outH = Math.Max(16, _srcH / _step);
                    _top = int.MaxValue; _bottom = -1; _left = int.MaxValue; _right = -1; _columnWidth = -1;
                    _sws = ffmpeg.sws_getContext(_srcW, _srcH, format, _outW, _outH, AVPixelFormat.AV_PIX_FMT_GBRP16LE,
                        ffmpeg.SWS_POINT | ffmpeg.SWS_ACCURATE_RND | ffmpeg.SWS_FULL_CHR_H_INT, null, null, null);
                    if (_sws == null) return -1;
                    bool bt709 = frame->colorspace is AVColorSpace.AVCOL_SPC_BT709;
                    _kr = bt709 ? 0.2126f : 0.2627f; _kb = bt709 ? 0.0722f : 0.0593f;
                    int space = bt709 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_BT2020;
                    int* coefficients = ffmpeg.sws_getCoefficients(space);
                    if (coefficients != null)
                    {
                        var table = new int_array4();
                        for (int i = 0; i < 4; i++) table[(uint)i] = coefficients[i];
                        int sourceRange = frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
                        ffmpeg.sws_setColorspaceDetails(_sws, table, sourceRange, table, 1, 0, 1 << 16, 1 << 16);
                    }
                    if (_planeMemory != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(_planeMemory);
                    _planeStride = (_outW * 2 + 63) & ~63;
                    _planeMemory = System.Runtime.InteropServices.Marshal.AllocHGlobal(_planeStride * _outH * 3 + 64);
                    _planeG = (ushort*)_planeMemory;
                    _planeB = (ushort*)((byte*)_planeMemory + (long)_planeStride * _outH);
                    _planeR = (ushort*)((byte*)_planeMemory + (long)_planeStride * _outH * 2);
                }

                var planes = new byte_ptrArray4();
                var strides = new int_array4();
                planes[0] = (byte*)_planeG; planes[1] = (byte*)_planeB; planes[2] = (byte*)_planeR; planes[3] = null;
                strides[0] = _planeStride; strides[1] = _planeStride; strides[2] = _planeStride; strides[3] = 0;
                if (ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, _srcH, planes, strides) != _outH) return 0;

                // Bande nere: la prima e l'ultima riga (e colonna) con qualcosa di acceso.
                int lit = 0;
                while (lit < _outH && !RowLit(lit, 0, _outW - 1)) lit++;
                if (lit < _outH)
                {
                    int last = _outH - 1;
                    while (last > lit && !RowLit(last, 0, _outW - 1)) last--;
                    int first = 0, end = _outW - 1;
                    while (first < end && !ColumnLit(first, lit, last)) first++;
                    while (end > first && !ColumnLit(end, lit, last)) end--;
                    if (lit < _top) _top = lit;
                    if (last > _bottom) _bottom = last;
                    if (first < _left) _left = first;
                    if (end > _right) _right = end;
                }
                int y0 = 0, y1 = _outH - 1, x0 = 0, x1 = _outW - 1;
                if (_bottom >= _top && _right >= _left) { y0 = _top; y1 = _bottom; x0 = _left; x1 = _right; }
                int width = x1 - x0 + 1, count = width * (y1 - y0 + 1);
                ActiveWidth = Math.Min(_srcW, width * _step);
                ActiveHeight = Math.Min(_srcH, (y1 - y0 + 1) * _step);

                Array.Clear(Codes);
                Array.Clear(Bins);
                Above100 = Above400 = Above1000 = GamutPixels = In709 = InP3 = 0;
                float[] nitsLut = _nitsLut;
                short[] codeOf = _codeOfLut;
                byte[] binOf = _binOfLut;
                int[] codes = Codes, bins = Bins;
                double frameSum = 0, lumSum = 0;
                int topMax = 0, chromaPixels = 0;
                float yr = _yr, yg = _yg, yb = _yb;

                int[]? waveR = null, waveG = null, waveB = null, chroma = null, vector = null;
                int pictureStep = Math.Max(1, _outW / 240);
                if (scopes != null)
                {
                    scopes.Prepare(_outW / pictureStep, _outH / pictureStep);
                    waveR = scopes.WaveR; waveG = scopes.WaveG; waveB = scopes.WaveB; chroma = scopes.Chroma; vector = scopes.Vector;
                    if (_columnWidth != width)
                    {
                        _columnWidth = width;
                        _columnOf = new int[width];
                        for (int i = 0; i < width; i++) _columnOf[i] = Math.Min(ScopeData.WaveColumns - 1, i * ScopeData.WaveColumns / width) * ScopeData.WaveLevels;
                    }
                }
                int[] columnOf = _columnOf;
                const int ChromaN = ScopeData.ChromaSize, VectorN = ScopeData.VectorSize;
                float chromaX = ChromaN / (float)ScopeData.ChromaMaxX, chromaY = ChromaN / (float)ScopeData.ChromaMaxY;
                float vectorScale = VectorN / (2 * ScopeData.VectorRange);
                float kr = _kr, kb = _kb, kg = 1 - _kr - _kb, cbScale = 1 / (2 * (1 - _kb)), crScale = 1 / (2 * (1 - _kr));

                for (int y = y0; y <= y1; y++)
                {
                    ushort* g = Row(_planeG, y), b = Row(_planeB, y), r = Row(_planeR, y);
                    for (int x = x0; x <= x1; x++)
                    {
                        int ri = r[x] >> 4, gi = g[x] >> 4, bi = b[x] >> 4;
                        int top = ri > gi ? (ri > bi ? ri : bi) : (gi > bi ? gi : bi);
                        float nits = nitsLut[top];
                        frameSum += nits;
                        codes[codeOf[top]]++;
                        bins[binOf[top]]++;
                        if (top > topMax) topMax = top;
                        if (nits > 100) { Above100++; if (nits > 400) { Above400++; if (nits > 1000) Above1000++; } }

                        float lr = nitsLut[ri], lg = nitsLut[gi], lb = nitsLut[bi];
                        float luminance = yr * lr + yg * lg + yb * lb;
                        lumSum += luminance;

                        if (_measureGamut && nits > 1f)
                        {
                            // Lineare BT.2020 -> Rec.709 e P3-D65: un componente negativo
                            // significa che il colore sta fuori da quel triangolo.
                            float tolerance = -0.02f * nits;
                            GamutPixels++;
                            if (1.6605f * lr - 0.5876f * lg - 0.0728f * lb >= tolerance &&
                                -0.1246f * lr + 1.1329f * lg - 0.0083f * lb >= tolerance &&
                                -0.0182f * lr - 0.1006f * lg + 1.1187f * lb >= tolerance)
                                In709++;
                            else if (1.3436f * lr - 0.2822f * lg - 0.0614f * lb >= tolerance &&
                                     -0.0653f * lr + 1.0758f * lg - 0.0105f * lb >= tolerance &&
                                     0.0028f * lr - 0.0196f * lg + 1.0168f * lb >= tolerance)
                                InP3++;
                        }

                        if (waveR != null)
                        {
                            int column = columnOf[x - x0];
                            waveR[column + (codeOf[ri] >> 2)]++;
                            waveG![column + (codeOf[gi] >> 2)]++;
                            waveB![column + (codeOf[bi] >> 2)]++;
                            if (nits > 2f)
                            {
                                float cieX = _xr * lr + _xg * lg + _xb * lb, sum = cieX + luminance + _zr * lr + _zg * lg + _zb * lb;
                                if (sum > 0)
                                {
                                    int cx = (int)(cieX / sum * chromaX), cy = (int)(luminance / sum * chromaY);
                                    if ((uint)cx < ChromaN && (uint)cy < ChromaN) { chroma![cy * ChromaN + cx]++; chromaPixels++; }
                                }
                            }
                            float rp = ri * (1f / 4095f), gp = gi * (1f / 4095f), bp = bi * (1f / 4095f);
                            float luma = kr * rp + kg * gp + kb * bp;
                            int vx = (int)(((bp - luma) * cbScale + ScopeData.VectorRange) * vectorScale);
                            int vy = (int)((ScopeData.VectorRange - (rp - luma) * crScale) * vectorScale);
                            if (vx < 0) vx = 0; else if (vx >= VectorN) vx = VectorN - 1;
                            if (vy < 0) vy = 0; else if (vy >= VectorN) vy = VectorN - 1;
                            vector![vy * VectorN + vx]++;
                        }
                    }
                }
                Count = count;

                if (scopes != null)
                {
                    // Falsi colori: l'immagine intera (bande comprese), un pixel ogni pictureStep.
                    uint[] colors = FalseColorLut();
                    fixed (byte* picture = scopes.FalseColor)
                    {
                        uint* pixel = (uint*)picture;
                        for (int ty = 0; ty < scopes.PictureHeight; ty++)
                        {
                            ushort* g = Row(_planeG, ty * pictureStep), b = Row(_planeB, ty * pictureStep), r = Row(_planeR, ty * pictureStep);
                            for (int tx = 0; tx < scopes.PictureWidth; tx++)
                            {
                                int x = tx * pictureStep;
                                int ri = r[x] >> 4, gi = g[x] >> 4, bi = b[x] >> 4;
                                int top = ri > gi ? (ri > bi ? ri : bi) : (gi > bi ? gi : bi);
                                *pixel++ = colors[codeOf[top]];
                            }
                        }
                    }
                    scopes.SamplesPerColumn = Math.Max(1, count / ScopeData.WaveColumns);
                    scopes.ChromaPixels = chromaPixels;
                    scopes.Pixels = count;
                }

                // Percentili sull'asse PQ a 10 bit. Picco: 99,99° percentile (un pixel isolato non conta).
                int skip = Math.Max(1, count / 10000), seen = 0, code = 1023;
                for (; code > 0; code--) { seen += codes[code]; if (seen >= skip) break; }
                PeakNits = (float)PqToNits(code / 1023.0);
                MaxNits = nitsLut[topMax];
                AverageNits = (float)(frameSum / count);
                LuminanceNits = (float)(lumSum / count);
                Percentiles(codes, count, out BlackNits, out MedianNits, out P90Nits, out P99Nits);
                return 1;
            }

            public void Dispose()
            {
                if (_planeMemory != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_planeMemory); _planeMemory = IntPtr.Zero; }
                if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            }
        }

        /// <summary>Nero (0,1° percentile), mediana, 90° e 99° percentile da un istogramma sull'asse PQ a 10 bit.</summary>
        private static void Percentiles<T>(T[] codes, double count, out float black, out float median, out float p90, out float p99) where T : struct, IConvertible
        {
            black = median = p90 = p99 = 0;
            if (count <= 0) return;
            double seen = 0;
            int stage = 0;
            double[] targets = { count * 0.001, count * 0.5, count * 0.9, count * 0.99 };
            for (int code = 0; code < codes.Length && stage < 4; code++)
            {
                seen += codes[code].ToDouble(null);
                while (stage < 4 && seen >= targets[stage])
                {
                    float nits = (float)PqToNits(code / 1023.0);
                    if (stage == 0) black = nits; else if (stage == 1) median = nits; else if (stage == 2) p90 = nits; else p99 = nits;
                    stage++;
                }
            }
        }

        /// <summary>
        /// Analisi in tempo reale del film in riproduzione, con un decodificatore proprio (la
        /// riproduzione non viene toccata e il risultato non dipende dal renderer). Due modi:
        /// - continuo (<see cref="OpenContinuous"/> + <see cref="Start"/>): un thread decodifica
        ///   il film fotogramma per fotogramma seguendo la posizione di riproduzione e misura
        ///   ogni fotogramma quando la riproduzione ci arriva, strumenti compresi. Se il
        ///   computer non regge la decodifica in tempo reale scende da solo ai soli fotogrammi
        ///   di riferimento e poi ai soli fotogrammi chiave;
        /// - a richiesta (<see cref="Measure"/>): un solo fotogramma chiave per chiamata.
        /// </summary>
        public sealed class LiveSession : IDisposable
        {
            private readonly object _gate = new();
            private readonly object _swap = new();
            private readonly object _clock = new();
            private AVFormatContext* _fmt;
            private AVCodecContext* _dec;
            private AVPacket* _pkt;
            private AVFrame* _frame;
            private FrameMeter? _meter;
            private int _videoIndex;
            private double _timeBase = 1;
            private long _startTs;
            private long _lastKeyPts = long.MinValue;
            private FrameStats? _last;
            private bool _disposed;

            // Modo continuo.
            private Thread? _thread;
            private volatile bool _stop;
            private ScopeData _front = new(), _back = new();
            private FrameStats? _latest;
            private long _version;
            private double _playhead = double.NaN;
            private long _playheadStamp;
            private bool _playing;
            private int _jumps;
            private volatile int _detail;
            private volatile float _rate;

            /// <summary>Formato dichiarato dal file (HDR o no, Dolby Vision, dimensioni).</summary>
            public Result Format { get; } = new() { Version = CurrentVersion };
            public bool CanMeasure => _meter != null;
            /// <summary>Cambia a ogni fotogramma misurato in modo continuo.</summary>
            public long Version => Interlocked.Read(ref _version);
            /// <summary>0 = ogni fotogramma, 1 = solo i fotogrammi di riferimento, 2 = solo i fotogrammi chiave.</summary>
            public int Detail => _detail;
            /// <summary>Fotogrammi misurati al secondo.</summary>
            public float MeasuredRate => _rate;

            private LiveSession() { }

            /// <summary>Apre il file. Va chiamato fuori dal thread dell'interfaccia.</summary>
            public static LiveSession Open(string path) => Open(path, false);

            /// <summary>Come <see cref="Open(string)"/>, con il decodificatore pronto a seguire la riproduzione.</summary>
            public static LiveSession OpenContinuous(string path) => Open(path, true);

            private static LiveSession Open(string path, bool continuous)
            {
                var session = new LiveSession();
                try
                {
                    FFmpegBootstrap.Ensure();
                    AVFormatContext* fmt = null;
                    bool opened = OpenVideo(path, session.Format, &fmt, out session._videoIndex);
                    session._fmt = fmt;
                    if (!opened || !session.Format.IsHdr || session.Format.BaseLayerNotMeasurable) return session;

                    AVStream* stream = fmt->streams[session._videoIndex];
                    session._dec = OpenDecoder(stream->codecpar, continuous);
                    if (session._dec == null) { session.Format.Error = "decoder"; return session; }
                    session._timeBase = Math.Max(ffmpeg.av_q2d(stream->time_base), 1e-12);
                    session._startTs = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
                    session._pkt = ffmpeg.av_packet_alloc();
                    session._frame = ffmpeg.av_frame_alloc();
                    session._meter = new FrameMeter(session.Format.Transfer == "HLG", session.Format.Primaries);
                }
                catch (Exception ex) { session.Format.Error = ex.GetType().Name; }
                return session;
            }

            private FrameStats BuildStats(double time)
            {
                var meter = _meter!;
                double count = meter.Count;
                var stats = new FrameStats
                {
                    Time = time,
                    PeakNits = meter.PeakNits,
                    AverageNits = meter.AverageNits,
                    MaxNits = meter.MaxNits,
                    LuminanceNits = meter.LuminanceNits,
                    BlackNits = meter.BlackNits,
                    MedianNits = meter.MedianNits,
                    P90Nits = meter.P90Nits,
                    P99Nits = meter.P99Nits,
                    ActiveWidth = meter.ActiveWidth,
                    ActiveHeight = meter.ActiveHeight,
                    ShareAbove100 = meter.Above100 / count,
                    ShareAbove400 = meter.Above400 / count,
                    ShareAbove1000 = meter.Above1000 / count,
                    GamutMeasured = meter.GamutPixels > 0
                };
                for (int i = 0; i < HistogramBins; i++) stats.Histogram[i] = meter.Bins[i] / count;
                if (stats.GamutMeasured)
                {
                    stats.GamutRec709 = meter.In709 / (double)meter.GamutPixels;
                    stats.GamutP3 = meter.InP3 / (double)meter.GamutPixels;
                    stats.GamutRec2020 = Math.Max(0, 1 - stats.GamutRec709 - stats.GamutP3);
                }
                return stats;
            }

            /// <summary>Misura il fotogramma chiave alla posizione data (secondi) o subito prima.
            /// Null se non c'e' nulla da misurare. Va chiamato fuori dal thread dell'interfaccia.</summary>
            public FrameStats? Measure(double seconds)
            {
                lock (_gate)
                {
                    if (_disposed || _meter == null || _thread != null || !double.IsFinite(seconds)) return null;
                    try
                    {
                        long targetTs = _startTs + (long)Math.Round(Math.Max(0, seconds) / _timeBase);
                        if (ffmpeg.avformat_seek_file(_fmt, _videoIndex, long.MinValue, targetTs, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0 &&
                            ffmpeg.av_seek_frame(_fmt, _videoIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
                            return null;

                        bool gotFrame = false;
                        for (int read = 0; read < 600 && !gotFrame; read++)
                        {
                            if (ffmpeg.av_read_frame(_fmt, _pkt) < 0) break;
                            bool key = _pkt->stream_index == _videoIndex && (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                            if (key)
                            {
                                // Stesso fotogramma chiave dell'ultima misura (pausa, o posizione
                                // ancora dentro lo stesso gruppo di fotogrammi): niente da decodificare.
                                long keyPts = _pkt->pts != ffmpeg.AV_NOPTS_VALUE ? _pkt->pts : _pkt->dts;
                                if (keyPts == _lastKeyPts && _last != null)
                                {
                                    ffmpeg.av_packet_unref(_pkt);
                                    return _last;
                                }
                                ffmpeg.avcodec_flush_buffers(_dec);
                                if (ffmpeg.avcodec_send_packet(_dec, _pkt) >= 0)
                                {
                                    ffmpeg.av_packet_unref(_pkt);
                                    ffmpeg.avcodec_send_packet(_dec, null);
                                    gotFrame = ffmpeg.avcodec_receive_frame(_dec, _frame) >= 0;
                                    if (gotFrame) _lastKeyPts = keyPts;
                                    break;
                                }
                            }
                            ffmpeg.av_packet_unref(_pkt);
                        }
                        if (!gotFrame || _frame->width <= 0 || _frame->height <= 0 || _frame->data[0] == null)
                        {
                            ffmpeg.av_frame_unref(_frame);
                            return null;
                        }

                        long framePts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _frame->best_effort_timestamp : _frame->pts;
                        int measured = _meter.Measure(_frame);
                        ffmpeg.av_frame_unref(_frame);
                        if (measured <= 0 || _meter.Count <= 0) return null;
                        return _last = BuildStats(Math.Max(0, (framePts - _startTs) * _timeBase));
                    }
                    catch { return null; }
                }
            }

            // ===== Modo continuo =====

            /// <summary>Posizione di riproduzione (secondi), da aggiornare qualche volta al secondo.
            /// Zero o NaN: il film non e' in riproduzione.</summary>
            public void SetPlayhead(double seconds)
            {
                lock (_clock)
                {
                    bool valid = double.IsFinite(seconds) && seconds > 0;
                    if (valid && double.IsFinite(_playhead))
                    {
                        double moved = seconds - _playhead;
                        _playing = moved > 0.0005 && moved < 1.0;
                        if (Math.Abs(moved) > 1.0) _jumps++;      // l'utente si e' spostato nel film
                    }
                    else _playing = false;
                    _playhead = valid ? seconds : double.NaN;
                    _playheadStamp = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            // Fra un aggiornamento e l'altro la posizione avanza con l'orologio (al massimo 0,3 s).
            private double Playhead(out int jumps)
            {
                lock (_clock)
                {
                    jumps = _jumps;
                    if (!double.IsFinite(_playhead)) return double.NaN;
                    return _playing ? _playhead + Math.Min(0.3, System.Diagnostics.Stopwatch.GetElapsedTime(_playheadStamp).TotalSeconds) : _playhead;
                }
            }

            /// <summary>Avvia la misura continua (dopo <see cref="OpenContinuous"/>).</summary>
            public void Start()
            {
                lock (_gate)
                {
                    if (_disposed || _meter == null || _thread != null) return;
                    _thread = new Thread(Run) { IsBackground = true, Name = "HDR live analysis", Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }
            }

            /// <summary>Ultimo fotogramma misurato; <paramref name="render"/> riceve gli strumenti
            /// di quel fotogramma (validi solo durante la chiamata).</summary>
            public FrameStats? Read(Action<ScopeData>? render = null)
            {
                lock (_swap)
                {
                    if (_latest != null) render?.Invoke(_front);
                    return _latest;
                }
            }

            private bool SeekTo(double seconds)
            {
                long targetTs = _startTs + (long)Math.Round(Math.Max(0, seconds) / _timeBase);
                if (ffmpeg.avformat_seek_file(_fmt, _videoIndex, long.MinValue, targetTs, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0 &&
                    ffmpeg.av_seek_frame(_fmt, _videoIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
                    return false;
                ffmpeg.avcodec_flush_buffers(_dec);
                return true;
            }

            private void ApplyDetail(int detail)
            {
                _detail = detail;
                var discard = detail >= 1 ? AVDiscard.AVDISCARD_NONREF : AVDiscard.AVDISCARD_DEFAULT;
                _dec->skip_frame = discard;
                _dec->skip_loop_filter = discard;
            }

            private void Run()
            {
                bool positioned = false, firstAfterSeek = false;
                double lastPts = double.NaN, bestGap = double.MaxValue;
                int jumpsSeen = 0, measuredInWindow = 0;
                long windowStart = System.Diagnostics.Stopwatch.GetTimestamp(), lastLateMeasure = 0;
                var fallBehind = new Queue<long>();
                try
                {
                    while (!_stop)
                    {
                        double position = Playhead(out int jumps);
                        if (!double.IsFinite(position)) { positioned = false; Thread.Sleep(80); continue; }

                        bool behind = positioned && position - lastPts > 1.5 && position - lastPts > bestGap + 1.0;
                        if (!positioned || jumps != jumpsSeen || position < lastPts - 0.6 || behind)
                        {
                            if (behind && jumps == jumpsSeen)
                            {
                                // La decodifica non tiene il passo della riproduzione: tre volte in
                                // venti secondi e si rinuncia a un po' di dettaglio.
                                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                fallBehind.Enqueue(now);
                                while (fallBehind.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(fallBehind.Peek(), now).TotalSeconds > 20) fallBehind.Dequeue();
                                if (fallBehind.Count >= 3 && _detail < 2) { ApplyDetail(_detail + 1); fallBehind.Clear(); }
                            }
                            jumpsSeen = jumps;
                            if (!SeekTo(position)) { positioned = false; Thread.Sleep(150); continue; }
                            positioned = true; firstAfterSeek = true; lastPts = position; bestGap = double.MaxValue;
                        }

                        if (ffmpeg.av_read_frame(_fmt, _pkt) < 0) { Thread.Sleep(120); continue; } // fine del file
                        if (_pkt->stream_index != _videoIndex) { ffmpeg.av_packet_unref(_pkt); continue; }
                        if (_detail >= 2)
                        {
                            if ((_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0) { ffmpeg.av_packet_unref(_pkt); continue; }
                            ffmpeg.avcodec_flush_buffers(_dec);
                            ffmpeg.avcodec_send_packet(_dec, _pkt);
                            ffmpeg.av_packet_unref(_pkt);
                            ffmpeg.avcodec_send_packet(_dec, null);
                        }
                        else
                        {
                            ffmpeg.avcodec_send_packet(_dec, _pkt);
                            ffmpeg.av_packet_unref(_pkt);
                        }

                        while (!_stop && ffmpeg.avcodec_receive_frame(_dec, _frame) >= 0)
                        {
                            long framePts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _frame->best_effort_timestamp : _frame->pts;
                            double pts = Math.Max(0, (framePts - _startTs) * _timeBase);
                            lastPts = pts;
                            double current = Playhead(out jumps);
                            bool measure = false;
                            if (double.IsFinite(current) && jumps == jumpsSeen && _frame->width > 0 && _frame->data[0] != null)
                            {
                                double gap = current - pts;
                                if (gap < bestGap) bestGap = gap;
                                if (firstAfterSeek) measure = true;      // il fotogramma chiave subito prima: si vede qualcosa senza aspettare
                                else if (gap > 0.15)
                                {
                                    // In ritardo: si recupera senza misurare ogni fotogramma (ma lo
                                    // schermo non resta fermo se il ritardo e' piccolo).
                                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                    if (gap <= 1.5 && System.Diagnostics.Stopwatch.GetElapsedTime(lastLateMeasure, now).TotalMilliseconds >= 100) { measure = true; lastLateMeasure = now; }
                                }
                                else
                                {
                                    // In anticipo: si aspetta che la riproduzione arrivi a questo fotogramma.
                                    double waitStart = current;
                                    while (!_stop)
                                    {
                                        current = Playhead(out jumps);
                                        if (!double.IsFinite(current) || jumps != jumpsSeen || current < waitStart - 0.6) break;
                                        if (pts <= current + 0.005) { measure = true; break; }
                                        Thread.Sleep(4);
                                    }
                                }
                            }
                            firstAfterSeek = false;
                            if (measure && _meter!.Measure(_frame, _back) > 0 && _meter.Count > 0)
                            {
                                var stats = BuildStats(pts);
                                lock (_swap)
                                {
                                    (_front, _back) = (_back, _front);
                                    _latest = stats;
                                }
                                Interlocked.Increment(ref _version);
                                measuredInWindow++;
                            }
                            ffmpeg.av_frame_unref(_frame);

                            double window = System.Diagnostics.Stopwatch.GetElapsedTime(windowStart).TotalSeconds;
                            if (window >= 1)
                            {
                                _rate = (float)(measuredInWindow / window);
                                measuredInWindow = 0;
                                windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            }
                        }
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                _stop = true;
                Thread? thread;
                lock (_gate) thread = _thread;
                try { thread?.Join(4000); } catch { }
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    // Un thread che non si e' fermato in tempo sta ancora usando il decodificatore:
                    // meglio lasciare la memoria al processo che liberarla sotto di lui.
                    if (thread != null && thread.IsAlive) return;
                    _meter?.Dispose();
                    _meter = null;
                    AVFrame* frame = _frame; AVPacket* pkt = _pkt; AVCodecContext* dec = _dec; AVFormatContext* fmt = _fmt;
                    _frame = null; _pkt = null; _dec = null; _fmt = null;
                    if (frame != null) ffmpeg.av_frame_free(&frame);
                    if (pkt != null) ffmpeg.av_packet_free(&pkt);
                    if (dec != null) ffmpeg.avcodec_free_context(&dec);
                    if (fmt != null) ffmpeg.avformat_close_input(&fmt);
                }
            }
        }

        /// <summary>Somme dell'analisi dell'intero film.</summary>
        private sealed class Totals
        {
            public readonly double[] Histogram = new double[HistogramBins];
            public readonly long[] Codes = new long[1024];
            public double Pixels, Above100, Above400, Above1000, SumAverage, SumLuminance, GamutPixels, In709, InP3;

            public void Add(FrameMeter meter)
            {
                for (int i = 0; i < HistogramBins; i++) Histogram[i] += meter.Bins[i];
                for (int i = 0; i < Codes.Length; i++) Codes[i] += meter.Codes[i];
                Pixels += meter.Count;
                Above100 += meter.Above100; Above400 += meter.Above400; Above1000 += meter.Above1000;
                GamutPixels += meter.GamutPixels; In709 += meter.In709; InP3 += meter.InP3;
                SumAverage += meter.AverageNits; SumLuminance += meter.LuminanceNits;
            }
        }

        private static void Summarize(Result result, Totals totals, bool measureGamut)
        {
            double pixels = totals.Pixels;
            if (pixels <= 0) return;
            var shares = new double[HistogramBins];
            for (int i = 0; i < HistogramBins; i++) shares[i] = totals.Histogram[i] / pixels;
            result.Histogram = shares;
            result.ShareAbove100 = totals.Above100 / pixels;
            result.ShareAbove400 = totals.Above400 / pixels;
            result.ShareAbove1000 = totals.Above1000 / pixels;
            int frames = Math.Max(1, result.Samples.Count);
            result.OverallAverageNits = (float)(totals.SumAverage / frames);
            result.LuminanceAverageNits = (float)(totals.SumLuminance / frames);
            Percentiles(totals.Codes, pixels, out float black, out float median, out float p90, out float p99);
            result.BlackNits = black; result.MedianNits = median; result.P90Nits = p90; result.P99Nits = p99;
            var peaks = new List<float>(result.Samples.Count);
            foreach (var sample in result.Samples) peaks.Add(sample.PeakNits);
            peaks.Sort();
            result.MedianPeakNits = peaks.Count > 0 ? peaks[peaks.Count / 2] : 0;
            result.GamutMeasured = measureGamut && totals.GamutPixels > 0;
            if (result.GamutMeasured)
            {
                result.GamutRec709 = totals.In709 / totals.GamutPixels;
                result.GamutP3 = totals.InP3 / totals.GamutPixels;
                result.GamutRec2020 = Math.Max(0, 1 - result.GamutRec709 - result.GamutP3);
            }
        }

        private static void ReadStreamSideData(AVCodecParameters* par, Result result)
        {
            for (int i = 0; i < par->nb_coded_side_data; i++)
            {
                AVPacketSideData* side = &par->coded_side_data[i];
                if (side->data == null) continue;
                switch (side->type)
                {
                    case AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL:
                        ApplyLightLevel((AVContentLightMetadata*)side->data, result);
                        break;
                    case AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA:
                        ApplyMastering((AVMasteringDisplayMetadata*)side->data, result);
                        break;
                    case AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF:
                        // AVDOVIDecoderConfigurationRecord: otto byte in fila (versione, profilo,
                        // livello, tre flag, compatibilita' dello strato base).
                        if ((long)side->size >= 8)
                        {
                            result.DolbyVisionProfile = side->data[2];
                            result.DolbyVisionCompatibility = side->data[7];
                        }
                        break;
                }
            }
        }

        private static void ReadFrameSideData(AVFrame* frame, Result result)
        {
            if (result.DeclaredMaxCll == null)
            {
                var light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
                if (light != null && light->data != null) ApplyLightLevel((AVContentLightMetadata*)light->data, result);
            }
            if (result.MasteringMaxNits == null)
            {
                var mastering = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
                if (mastering != null && mastering->data != null) ApplyMastering((AVMasteringDisplayMetadata*)mastering->data, result);
            }
        }

        private static void ApplyLightLevel(AVContentLightMetadata* light, Result result)
        {
            if (light->MaxCLL > 0) result.DeclaredMaxCll = (int)light->MaxCLL;
            if (light->MaxFALL > 0) result.DeclaredMaxFall = (int)light->MaxFALL;
        }

        private static void ApplyMastering(AVMasteringDisplayMetadata* mastering, Result result)
        {
            if (mastering->has_luminance == 0) return;
            double max = ffmpeg.av_q2d(mastering->max_luminance), min = ffmpeg.av_q2d(mastering->min_luminance);
            if (max > 0) { result.MasteringMaxNits = max; result.MasteringMinNits = min; }
        }
    }
}
