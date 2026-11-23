#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#include <CoreVideo/CoreVideo.h>
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/pixfmt.h>
#include "vt_protocol.h"

static const char *mapping_path;

static void remove_mapping(void)
{
    if (mapping_path) unlink(mapping_path);
}

static int transfer(int fd, void *buffer, size_t size, int write_mode)
{
    uint8_t *p = buffer;
    while (size) {
        ssize_t n = write_mode ? send(fd, p, size, 0) : recv(fd, p, size, 0);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return 0;
        p += n;
        size -= (size_t)n;
    }
    return 1;
}

static enum AVPixelFormat pick_vt(AVCodecContext *context, const enum AVPixelFormat *formats)
{
    (void)context;
    for (; *formats != AV_PIX_FMT_NONE; ++formats)
        if (*formats == AV_PIX_FMT_VIDEOTOOLBOX) return *formats;
    return AV_PIX_FMT_NONE;
}

static int open_decoder(int fd, int extra_size, AVCodecContext **decoder)
{
    VtOpen info;
    avcodec_free_context(decoder);
    if (extra_size < 0 || extra_size > VT_EXTRA_LIMIT || !transfer(fd, &info, sizeof(info), 0)) return AVERROR(EINVAL);
    const AVCodec *hevc = avcodec_find_decoder(AV_CODEC_ID_HEVC);
    AVCodecContext *ctx = hevc ? avcodec_alloc_context3(hevc) : NULL;
    if (!ctx) return AVERROR(ENOMEM);
    ctx->extradata = av_mallocz((size_t)extra_size + AV_INPUT_BUFFER_PADDING_SIZE);
    if (!ctx->extradata || !transfer(fd, ctx->extradata, (size_t)extra_size, 0)) {
        avcodec_free_context(&ctx);
        return AVERROR(EIO);
    }
    ctx->extradata_size = extra_size;
    ctx->width = info.width;
    ctx->height = info.height;
    ctx->profile = info.profile;
    ctx->level = info.level;
    ctx->color_range = info.range;
    ctx->color_primaries = info.primaries;
    ctx->color_trc = info.transfer;
    ctx->colorspace = info.colorspace;
    ctx->chroma_sample_location = info.chroma;
    ctx->pkt_timebase = (AVRational){info.tb_num, info.tb_den};
    ctx->thread_count = 1;
    ctx->get_format = pick_vt;
    int status = av_hwdevice_ctx_create(&ctx->hw_device_ctx, AV_HWDEVICE_TYPE_VIDEOTOOLBOX, NULL, NULL, 0);
    if (status >= 0) status = avcodec_open2(ctx, hevc, NULL);
    if (status < 0) avcodec_free_context(&ctx);
    *decoder = ctx;
    return status;
}

static int send_packet(int fd, AVCodecContext *decoder, AVPacket *packet, int size)
{
    VtPacket info;
    av_packet_unref(packet);
    if (size == -1) return avcodec_send_packet(decoder, NULL);
    if (size < 0 || size > VT_PACKET_LIMIT || !transfer(fd, &info, sizeof(info), 0))
        return AVERROR(EINVAL);
    int ret = av_new_packet(packet, size);
    if (ret < 0) return ret;
    if (!transfer(fd, packet->data, (size_t)size, 0)) return AVERROR(EIO);
    packet->pts = info.pts;
    packet->dts = info.dts;
    packet->duration = info.duration;
    packet->flags = info.flags;
    return avcodec_send_packet(decoder, packet);
}

static uint32_t copy_side(const AVFrame *frame, enum AVFrameSideDataType type, uint8_t *dst, size_t capacity)
{
    AVFrameSideData *side = av_frame_get_side_data(frame, type);
    if (!side || side->size > capacity) return 0;
    memcpy(dst, side->data, side->size);
    return (uint32_t)side->size;
}

static int share_frame(const AVFrame *frame, uint8_t *mapping, VtFrame *info)
{
    if (frame->format != AV_PIX_FMT_VIDEOTOOLBOX || !frame->data[3]) return AVERROR(ENOSYS);
    CVPixelBufferRef pixels = (CVPixelBufferRef)frame->data[3];
    OSType type = CVPixelBufferGetPixelFormatType(pixels);
    int format = type == kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange
              || type == kCVPixelFormatType_420YpCbCr10BiPlanarFullRange ? VT_P010
              : type == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
              || type == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange ? VT_NV12 : 0;
    int width = frame->width, height = frame->height;
    if (!format || width <= 0 || height <= 0 || width > VT_MAX_WIDTH || height > VT_MAX_HEIGHT
        || (width | height) & 1 || CVPixelBufferGetPlaneCount(pixels) != 2) return AVERROR(ENOSYS);
    int stride = (width * (format == VT_P010 ? 2 : 1) + 63) & ~63;
    if ((size_t)stride * (height + height / 2) > VT_SLOT_BYTES) return AVERROR(EINVAL);
    uint32_t *states = (uint32_t *)mapping;
    int slot = -1;
    for (int i = 0; i < VT_SLOTS; i++) {
        uint32_t free_slot = 0;
        if (__atomic_compare_exchange_n(&states[i], &free_slot, 1, 0, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED)) {
            slot = i;
            break;
        }
    }
    if (slot < 0) return AVERROR(ENOMEM);
    if (CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) {
        __atomic_store_n(&states[slot], 0, __ATOMIC_RELEASE);
        return AVERROR(EIO);
    }
    uint8_t *dst = vt_slot(mapping, slot);
    int bytes = width * (format == VT_P010 ? 2 : 1);
    for (int plane = 0; plane < 2; plane++) {
        const uint8_t *src = CVPixelBufferGetBaseAddressOfPlane(pixels, plane);
        size_t pitch = CVPixelBufferGetBytesPerRowOfPlane(pixels, plane);
        int rows = plane ? height / 2 : height;
        if (!src || pitch < (size_t)bytes || CVPixelBufferGetHeightOfPlane(pixels, plane) < (size_t)rows) {
            CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
            __atomic_store_n(&states[slot], 0, __ATOMIC_RELEASE);
            return AVERROR(EIO);
        }
        if (pitch == (size_t)stride && bytes == stride) {
            memcpy(dst, src, pitch * rows);
            dst += pitch * rows;
        } else {
            for (int y = 0; y < rows; y++, dst += stride, src += pitch)
                memcpy(dst, src, bytes);
        }
    }
    CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    *info = (VtFrame){
        .pts = frame->pts, .dts = frame->pkt_dts, .best = frame->best_effort_timestamp,
        .duration = frame->duration, .slot = slot, .format = format,
        .width = width, .height = height, .stride = stride,
        .sar_num = frame->sample_aspect_ratio.num, .sar_den = frame->sample_aspect_ratio.den,
        .picture = frame->pict_type, .flags = frame->flags, .repeat = frame->repeat_pict,
        .range = frame->color_range, .primaries = frame->color_primaries, .transfer = frame->color_trc,
        .colorspace = frame->colorspace, .chroma = frame->chroma_location,
    };
    AVFrameSideData *side = av_frame_get_side_data(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    if (side && side->size == sizeof(AVMasteringDisplayMetadata)) {
        const AVMasteringDisplayMetadata *m = (const AVMasteringDisplayMetadata *)side->data;
        memcpy(info->mastering.primaries, m->display_primaries, sizeof(m->display_primaries));
        memcpy(info->mastering.white_point, m->white_point, sizeof(m->white_point));
        memcpy(info->mastering.min_luminance, &m->min_luminance, sizeof(m->min_luminance));
        memcpy(info->mastering.max_luminance, &m->max_luminance, sizeof(m->max_luminance));
        info->mastering.has_primaries = m->has_primaries;
        info->mastering.has_luminance = m->has_luminance;
        info->has_mastering = 1;
    }
    info->light_size = copy_side(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, info->light, sizeof(info->light));
    __atomic_store_n(&states[slot], 2, __ATOMIC_RELEASE);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc != 2) return 2;
    const char *token = getenv("CINECORE_VT_TOKEN");
    if (!token || strlen(token) != VT_TOKEN_BYTES) return 2;
    signal(SIGPIPE, SIG_IGN);
    int map_fd = open(argv[1], O_CREAT | O_EXCL | O_RDWR, 0600);
    if (map_fd < 0) { perror("mmap file"); return 1; }
    mapping_path = argv[1];
    atexit(remove_mapping);
    if (ftruncate(map_fd, VT_MAP_BYTES)) { perror("mmap size"); close(map_fd); return 1; }
    uint8_t *mapping = mmap(NULL, VT_MAP_BYTES, PROT_READ | PROT_WRITE, MAP_SHARED, map_fd, 0);
    close(map_fd);
    if (mapping == MAP_FAILED) { perror("mmap"); return 1; }
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in addr = {.sin_len = sizeof(addr), .sin_family = AF_INET, .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    socklen_t len = sizeof(addr);
    if (listener < 0 || bind(listener, (struct sockaddr *)&addr, sizeof(addr)) ||
        listen(listener, 1) || getsockname(listener, (struct sockaddr *)&addr, &len)) {
        perror("listen"); return 1;
    }
    printf("%u\n", ntohs(addr.sin_port));
    fflush(stdout);
    struct pollfd pfd = {.fd = listener, .events = POLLIN};
    if (poll(&pfd, 1, 25000) <= 0) return 1;
    int fd = accept(listener, NULL, NULL);
    close(listener);
    int one = 1;
    struct timeval timeout = {5, 0};
    if (fd < 0) return 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one));
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    VtHello hello;
    if (!transfer(fd, &hello, sizeof(hello), 0) || hello.magic != VT_MAGIC ||
        memcmp(hello.token, token, VT_TOKEN_BYTES)) return 1;
    // The decoder can be idle for a pause or during graph reconfiguration.
    timeout = (struct timeval){0, 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    // Entrambi i processi hanno mappato l'area. Non lasciare un file leggibile sul disco.
    remove_mapping();
    mapping_path = NULL;
    AVCodecContext *decoder = NULL;
    AVPacket *packet = av_packet_alloc();
    AVFrame *frame = av_frame_alloc();
    if (!packet || !frame) return 1;
    unsigned frames = 0;
    for (VtCommand cmd; transfer(fd, &cmd, sizeof(cmd), 0);) {
        int32_t status = AVERROR(EINVAL);
        VtFrame info;
        if (cmd.op == VT_OPEN) status = open_decoder(fd, cmd.size, &decoder);
        else if (cmd.op == VT_CLOSE) { avcodec_free_context(&decoder); status = 0; }
        else if (decoder && cmd.op == VT_SEND) status = send_packet(fd, decoder, packet, cmd.size);
        else if (decoder && cmd.op == VT_FLUSH) { avcodec_flush_buffers(decoder); status = 0; }
        else if (decoder && cmd.op == VT_RECEIVE) {
            status = avcodec_receive_frame(decoder, frame);
            if (!status) status = share_frame(frame, mapping, &info);
            av_frame_unref(frame);
        }
        if (status == AVERROR(EAGAIN)) status = -11; /* Windows EAGAIN */
        if (!transfer(fd, &status, sizeof(status), 1)) break;
        if (cmd.op == VT_RECEIVE && !status) {
            frames++;
            if (!transfer(fd, &info, sizeof(info), 1)) break;
        }
    }
    struct rusage usage;
    if (!getrusage(RUSAGE_SELF, &usage))
        fprintf(stderr, "VideoToolbox frames=%u cpu_seconds=%.3f\n", frames,
            usage.ru_utime.tv_sec + usage.ru_stime.tv_sec + (usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1e6);
    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&decoder);
    munmap(mapping, VT_MAP_BYTES);
    close(fd);
    return 0;
}
