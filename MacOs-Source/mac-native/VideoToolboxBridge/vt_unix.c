// Decodifica HEVC con VideoToolbox DENTRO il processo di Wine (libreria nativa caricata dal ponte di LAV con
// NtQueryVirtualMemory(MemoryWineLoadUnixLibByName) e chiamata con __wine_unix_call_dispatcher).
// Stessa logica di vt_native.c (il vecchio programma separato), senza socket, senza wineserver e senza copia:
// il fotogramma resta nel CVPixelBuffer di VideoToolbox, bloccato in lettura finche' LAV non lo rilascia.
// Misura 23/11/2026: una chiamata costa ~0,04 us da codice x86 sotto FEX (il socket Winsock passava dal wineserver).
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <CoreVideo/CoreVideo.h>
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/mastering_display_metadata.h>
#include "vt_unix.h"

typedef unsigned int NTSTATUS;
typedef NTSTATUS (*unixlib_entry_t)(void *args);

static AVCodecContext *decoder;
static AVPacket *packet;

static int32_t win_error(int status)
{
    // Codici di errore come li vede l'FFmpeg di LAV (Windows): EAGAIN e' 11, non 35.
    if (status == AVERROR(EAGAIN)) return -11;
    if (status == AVERROR(ENOMEM)) return -12;
    if (status == AVERROR(EINVAL)) return -22;
    if (status == AVERROR(ENOSYS)) return -40;
    if (status == AVERROR(EIO)) return -5;
    return status;
}

static enum AVPixelFormat pick_vt(AVCodecContext *context, const enum AVPixelFormat *formats)
{
    (void)context;
    for (; *formats != AV_PIX_FMT_NONE; ++formats)
        if (*formats == AV_PIX_FMT_VIDEOTOOLBOX) return *formats;
    return AV_PIX_FMT_NONE;
}

static NTSTATUS vt_open(void *args)
{
    VtUnixOpen *p = args;
    avcodec_free_context(&decoder);
    p->status = AVERROR(EINVAL);
    if (p->extra_size < 0 || p->extra_size > VT_UNIX_EXTRA_LIMIT || (p->extra_size && !p->extra)) return 0;
    const AVCodec *hevc = avcodec_find_decoder(AV_CODEC_ID_HEVC);
    AVCodecContext *ctx = hevc ? avcodec_alloc_context3(hevc) : NULL;
    if (!ctx) { p->status = win_error(AVERROR(ENOMEM)); return 0; }
    ctx->extradata = av_mallocz((size_t)p->extra_size + AV_INPUT_BUFFER_PADDING_SIZE);
    if (!ctx->extradata) { avcodec_free_context(&ctx); p->status = win_error(AVERROR(ENOMEM)); return 0; }
    if (p->extra_size) memcpy(ctx->extradata, (const void *)(uintptr_t)p->extra, (size_t)p->extra_size);
    ctx->extradata_size = p->extra_size;
    ctx->width = p->width;
    ctx->height = p->height;
    ctx->profile = p->profile;
    ctx->level = p->level;
    ctx->color_range = p->range;
    ctx->color_primaries = p->primaries;
    ctx->color_trc = p->transfer;
    ctx->colorspace = p->colorspace;
    ctx->chroma_sample_location = p->chroma;
    ctx->pkt_timebase = (AVRational){p->tb_num, p->tb_den};
    ctx->thread_count = 1;
    ctx->get_format = pick_vt;
    int status = av_hwdevice_ctx_create(&ctx->hw_device_ctx, AV_HWDEVICE_TYPE_VIDEOTOOLBOX, NULL, NULL, 0);
    if (status >= 0) status = avcodec_open2(ctx, hevc, NULL);
    if (status < 0) avcodec_free_context(&ctx);
    decoder = ctx;
    if (!packet) packet = av_packet_alloc();
    p->status = packet ? win_error(status) : win_error(AVERROR(ENOMEM));
    return 0;
}

static NTSTATUS vt_send(void *args)
{
    VtUnixSend *p = args;
    if (!decoder || !packet) { p->status = win_error(AVERROR(EINVAL)); return 0; }
    av_packet_unref(packet);
    if (p->size < 0) { p->status = win_error(avcodec_send_packet(decoder, NULL)); return 0; }
    if (p->size > VT_UNIX_PACKET_LIMIT || !p->data) { p->status = win_error(AVERROR(EINVAL)); return 0; }
    int ret = av_new_packet(packet, p->size);
    if (ret < 0) { p->status = win_error(ret); return 0; }
    memcpy(packet->data, (const void *)(uintptr_t)p->data, (size_t)p->size);
    packet->pts = p->pts;
    packet->dts = p->dts;
    packet->duration = p->duration;
    packet->flags = p->flags;
    p->status = win_error(avcodec_send_packet(decoder, packet));
    return 0;
}

static uint32_t copy_side(const AVFrame *frame, enum AVFrameSideDataType type, uint8_t *dst, size_t capacity)
{
    AVFrameSideData *side = av_frame_get_side_data(frame, type);
    if (!side || side->size > capacity) return 0;
    memcpy(dst, side->data, side->size);
    return (uint32_t)side->size;
}

// Il fotogramma consegnato: AVFrame (che tiene il CVPixelBuffer) bloccato in lettura fino a vt_release.
typedef struct { AVFrame *frame; CVPixelBufferRef pixels; } Held;

static NTSTATUS vt_receive(void *args)
{
    VtUnixReceive *p = args;
    memset(&p->f, 0, sizeof(p->f));
    p->held = 0;
    if (!decoder) { p->status = win_error(AVERROR(EINVAL)); return 0; }
    AVFrame *frame = av_frame_alloc();
    if (!frame) { p->status = win_error(AVERROR(ENOMEM)); return 0; }
    int status = avcodec_receive_frame(decoder, frame);
    if (status < 0) { av_frame_free(&frame); p->status = win_error(status); return 0; }
    if (frame->format != AV_PIX_FMT_VIDEOTOOLBOX || !frame->data[3]) { av_frame_free(&frame); p->status = win_error(AVERROR(ENOSYS)); return 0; }
    CVPixelBufferRef pixels = (CVPixelBufferRef)frame->data[3];
    OSType type = CVPixelBufferGetPixelFormatType(pixels);
    int format = type == kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange
              || type == kCVPixelFormatType_420YpCbCr10BiPlanarFullRange ? VT_UNIX_P010
              : type == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
              || type == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange ? VT_UNIX_NV12 : 0;
    int width = frame->width, height = frame->height;
    if (!format || width <= 0 || height <= 0 || (width | height) & 1 || CVPixelBufferGetPlaneCount(pixels) != 2 ||
        CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) {
        av_frame_free(&frame);
        p->status = win_error(AVERROR(ENOSYS));
        return 0;
    }
    int bytes = width * (format == VT_UNIX_P010 ? 2 : 1);
    for (int plane = 0; plane < 2; plane++) {
        const uint8_t *base = CVPixelBufferGetBaseAddressOfPlane(pixels, plane);
        size_t pitch = CVPixelBufferGetBytesPerRowOfPlane(pixels, plane);
        size_t rows = (size_t)(plane ? height / 2 : height);
        if (!base || pitch < (size_t)bytes || CVPixelBufferGetHeightOfPlane(pixels, plane) < rows) {
            CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
            av_frame_free(&frame);
            p->status = win_error(AVERROR(EIO));
            return 0;
        }
        p->plane[plane] = (uint64_t)(uintptr_t)base;
        p->pitch[plane] = (int32_t)pitch;
        p->plane_bytes[plane] = (uint64_t)pitch * rows;
    }
    Held *held = malloc(sizeof(*held));
    if (!held) {
        CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
        av_frame_free(&frame);
        p->status = win_error(AVERROR(ENOMEM));
        return 0;
    }
    held->frame = frame; held->pixels = pixels;
    p->held = (uint64_t)(uintptr_t)held;
    VtFrame *f = &p->f;
    *f = (VtFrame){
        .pts = frame->pts, .dts = frame->pkt_dts, .best = frame->best_effort_timestamp,
        .duration = frame->duration, .slot = 0, .format = format,
        .width = width, .height = height, .stride = p->pitch[0],
        .sar_num = frame->sample_aspect_ratio.num, .sar_den = frame->sample_aspect_ratio.den,
        .picture = frame->pict_type, .flags = frame->flags, .repeat = frame->repeat_pict,
        .range = frame->color_range, .primaries = frame->color_primaries, .transfer = frame->color_trc,
        .colorspace = frame->colorspace, .chroma = frame->chroma_location,
    };
    AVFrameSideData *side = av_frame_get_side_data(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    if (side && side->size == sizeof(AVMasteringDisplayMetadata)) {
        const AVMasteringDisplayMetadata *m = (const AVMasteringDisplayMetadata *)side->data;
        memcpy(f->mastering.primaries, m->display_primaries, sizeof(m->display_primaries));
        memcpy(f->mastering.white_point, m->white_point, sizeof(m->white_point));
        memcpy(f->mastering.min_luminance, &m->min_luminance, sizeof(m->min_luminance));
        memcpy(f->mastering.max_luminance, &m->max_luminance, sizeof(m->max_luminance));
        f->mastering.has_primaries = m->has_primaries;
        f->mastering.has_luminance = m->has_luminance;
        f->has_mastering = 1;
    }
    f->light_size = copy_side(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, f->light, sizeof(f->light));
    p->status = 0;
    return 0;
}

static NTSTATUS vt_release(void *args)
{
    Held *held = (Held *)(uintptr_t)((VtUnixRelease *)args)->held;
    if (!held) return 0;
    CVPixelBufferUnlockBaseAddress(held->pixels, kCVPixelBufferLock_ReadOnly);
    av_frame_free(&held->frame);
    free(held);
    return 0;
}

static NTSTATUS vt_flush(void *args)
{
    (void)args;
    if (decoder) avcodec_flush_buffers(decoder);
    return 0;
}

static NTSTATUS vt_close(void *args)
{
    (void)args;
    avcodec_free_context(&decoder);
    av_packet_free(&packet);
    return 0;
}

__attribute__((visibility("default"))) const unixlib_entry_t __wine_unix_call_funcs[] =
{
    [VT_UNIX_OPEN] = vt_open,
    [VT_UNIX_SEND] = vt_send,
    [VT_UNIX_RECEIVE] = vt_receive,
    [VT_UNIX_RELEASE] = vt_release,
    [VT_UNIX_FLUSH] = vt_flush,
    [VT_UNIX_CLOSE] = vt_close,
};
