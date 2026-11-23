#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libavcodec/avcodec.h>
#include <libavcodec/version.h>
#include <libavutil/buffer.h>
#include <libavutil/frame.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/version.h>
#include "vt_protocol.h"
#include "vt_unix.h"

static SOCKET channel = INVALID_SOCKET;
static uint8_t *mapping;
static AVCodecContext *hardware;
static SRWLOCK decoder_lock = SRWLOCK_INIT;
static volatile LONG hardware_frames;
static volatile LONG transport_failed;
static int (*open_original)(AVCodecContext *, const AVCodec *, AVDictionary **);
static int (*send_original)(AVCodecContext *, const AVPacket *);
static int (*receive_original)(AVCodecContext *, AVFrame *);
static void (*flush_original)(AVCodecContext *);
static void (*free_original)(AVCodecContext **);
static AVBufferRef *(*buffer_create)(uint8_t *, size_t, void (*)(void *, uint8_t *), void *, int);
static AVFrameSideData *(*side_new)(AVFrame *, enum AVFrameSideDataType, size_t);
static void (*frame_unref)(AVFrame *);

// Decoder nello stesso processo (cinecorevt.so caricata per percorso, CINECORE_VT_UNIXLIB = percorso NT): niente socket,
// niente wineserver, niente copia del fotogramma. Senza la variabile si usa il programma separato via socket.
typedef LONG (WINAPI *QueryVirtualMemoryFn)(HANDLE, PVOID, ULONG, PVOID, SIZE_T, SIZE_T *);
typedef LONG (WINAPI *UnixDispatcherFn)(UINT64, unsigned int, void *);
static UINT64 unix_funcs;
static UnixDispatcherFn *unix_dispatcher;
static volatile LONG unix_tried;

static int open_unixlib(void)
{
    if (unix_funcs) return 1;
    if (InterlockedExchange(&unix_tried, 1)) return 0;
    wchar_t path[MAX_PATH * 2];
    DWORD length = GetEnvironmentVariableW(L"CINECORE_VT_UNIXLIB", path, sizeof(path)/sizeof(*path));
    if (!length || length >= sizeof(path)/sizeof(*path)) return 0;
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    QueryVirtualMemoryFn query = ntdll ? (QueryVirtualMemoryFn)(void (*)(void))GetProcAddress(ntdll, "NtQueryVirtualMemory") : NULL;
    UnixDispatcherFn *dispatcher = ntdll ? (UnixDispatcherFn *)GetProcAddress(ntdll, "__wine_unix_call_dispatcher") : NULL;
    if (!query || !dispatcher || !*dispatcher) return 0;
    struct { USHORT length, maximum; ULONG pad; wchar_t *buffer; } name = {
        (USHORT)(length * sizeof(wchar_t)), (USHORT)((length + 1) * sizeof(wchar_t)), 0, path};
    UINT64 result[2] = {0, 0};
    LONG status = query(GetCurrentProcess(), &name, 1002 /* MemoryWineLoadUnixLibByName */, result, sizeof(result), NULL);
    if (status || !result[1]) {
        fprintf(stderr, "cinecore-vt: libreria nativa non caricata (0x%lx)\n", (unsigned long)status);
        return 0;
    }
    unix_dispatcher = dispatcher;
    unix_funcs = result[1];
    fprintf(stderr, "cinecore-vt: decoder nello stesso processo\n");
    return 1;
}

static int unix_call(unsigned int code, void *args)
{
    return (*unix_dispatcher)(unix_funcs, code, args) == 0;
}

static int transfer(void *buffer, int size, int write_mode)
{
    char *p = buffer;
    while (size) {
        int n = write_mode ? send(channel, p, size, 0) : recv(channel, p, size, 0);
        if (n <= 0) {
            if (channel != INVALID_SOCKET) {
                closesocket(channel);
                channel = INVALID_SOCKET;
                WSACleanup();
            }
            if (hardware) InterlockedExchange(&transport_failed, 1);
            return 0;
        }
        p += n;
        size -= n;
    }
    return 1;
}

static int open_connection(void)
{
    if (channel != INVALID_SOCKET) return 1;
    wchar_t path[MAX_PATH * 2];
    char port_text[16], token[VT_TOKEN_BYTES + 1];
    DWORD path_length = GetEnvironmentVariableW(L"CINECORE_VT_MAP", path, sizeof(path)/sizeof(*path));
    DWORD port_length = GetEnvironmentVariableA("CINECORE_VT_PORT", port_text, sizeof(port_text));
    if (!path_length || path_length >= sizeof(path)/sizeof(*path) || !port_length || port_length >= sizeof(port_text) ||
        GetEnvironmentVariableA("CINECORE_VT_TOKEN", token, sizeof(token)) != VT_TOKEN_BYTES)
        return 0;
    long port = strtol(port_text, NULL, 10);
    if (port <= 0 || port > 65535) return 0;
    if (!mapping) {
        HANDLE file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        LARGE_INTEGER size;
        if (file == INVALID_HANDLE_VALUE) return 0;
        HANDLE section = GetFileSizeEx(file, &size) && size.QuadPart == VT_MAP_BYTES
            ? CreateFileMappingW(file, NULL, PAGE_READWRITE, 0, 0, NULL) : NULL;
        CloseHandle(file);
        if (!section) return 0;
        mapping = MapViewOfFile(section, FILE_MAP_WRITE, 0, 0, VT_MAP_BYTES);
        CloseHandle(section);
        if (!mapping) return 0;
    }
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data)) return 0;
    channel = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (channel == INVALID_SOCKET) { WSACleanup(); return 0; }
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons((uint16_t)port),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    int one = 1;
    DWORD timeout = 3000;
    setsockopt(channel, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof(one));
    setsockopt(channel, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
    setsockopt(channel, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));
    VtHello hello = {.magic = VT_MAGIC};
    memcpy(hello.token, token, VT_TOKEN_BYTES);
    if (!connect(channel, (const struct sockaddr *)&address, sizeof(address)) &&
        transfer(&hello, sizeof(hello), 1))
        return 1;
    if (channel != INVALID_SOCKET) {
        closesocket(channel);
        channel = INVALID_SOCKET;
        WSACleanup();
    }
    return 0;
}

static int request(int op)
{
    VtCommand cmd = {.op = op};
    int32_t status;
    return transfer(&cmd, sizeof(cmd), 1) && transfer(&status, sizeof(status), 0) ? status : AVERROR_EXTERNAL;
}

static int open_hardware(AVCodecContext *ctx)
{
    for (int i = 0; i < ctx->nb_coded_side_data; i++) {
        enum AVPacketSideDataType type = ctx->coded_side_data[i].type;
        if (type == AV_PKT_DATA_DOVI_CONF || type == AV_PKT_DATA_DYNAMIC_HDR10_PLUS) return 0;
    }
    if (ctx->width < 0 || ctx->height < 0 || ctx->width > VT_MAX_WIDTH || ctx->height > VT_MAX_HEIGHT ||
        ctx->extradata_size < 0 || ctx->extradata_size > VT_EXTRA_LIMIT ||
        (ctx->extradata_size && !ctx->extradata))
        return 0;
    if (open_unixlib()) {
        VtUnixOpen p = {ctx->width, ctx->height, ctx->profile, ctx->level, ctx->color_range, ctx->color_primaries,
            ctx->color_trc, ctx->colorspace, ctx->chroma_sample_location, ctx->pkt_timebase.num, ctx->pkt_timebase.den,
            ctx->extradata_size, (UINT64)(UINT_PTR)ctx->extradata, -1, 0};
        return unix_call(VT_UNIX_OPEN, &p) && p.status == 0;
    }
    if (!open_connection())
        return 0;
    VtCommand cmd = {.op = VT_OPEN, .size = ctx->extradata_size};
    VtOpen info = {ctx->width, ctx->height, ctx->profile, ctx->level, ctx->color_range, ctx->color_primaries,
        ctx->color_trc, ctx->colorspace, ctx->chroma_sample_location, ctx->pkt_timebase.num, ctx->pkt_timebase.den};
    int32_t status;
    return transfer(&cmd, sizeof(cmd), 1) && transfer(&info, sizeof(info), 1) &&
        transfer(ctx->extradata, ctx->extradata_size, 1) && transfer(&status, sizeof(status), 0) && status == 0;
}

static int open_hook(AVCodecContext *ctx, const AVCodec *codec, AVDictionary **options)
{
    if (!ctx || (codec ? codec->id : ctx->codec_id) != AV_CODEC_ID_HEVC)
        return open_original(ctx, codec, options);
    AcquireSRWLockExclusive(&decoder_lock);
    int use_hardware = !hardware && open_hardware(ctx);
    int threads = ctx->thread_count;
    if (use_hardware) ctx->thread_count = 1;
    int ret = open_original(ctx, codec, options);
    if (use_hardware && ret == 0) {
        hardware = ctx;
        fprintf(stderr, "cinecore-vt: HEVC hardware %dx%d (software threads requested %d)\n", ctx->width, ctx->height, threads);
    } else if (use_hardware) {
        if (unix_funcs) unix_call(VT_UNIX_CLOSE, NULL); else request(VT_CLOSE);
    }
    ReleaseSRWLockExclusive(&decoder_lock);
    return ret;
}

static int send_hook(AVCodecContext *ctx, const AVPacket *packet)
{
    AcquireSRWLockExclusive(&decoder_lock);
    if (ctx != hardware || !hardware) {
        ReleaseSRWLockExclusive(&decoder_lock);
        return send_original(ctx, packet);
    }
    int32_t status = AVERROR_EXTERNAL;
    int size = packet && packet->data ? packet->size : -1;
    if (size < -1 || size > VT_PACKET_LIMIT) {
        ReleaseSRWLockExclusive(&decoder_lock);
        return AVERROR(EINVAL);
    }
    if (unix_funcs) {
        VtUnixSend p = {(UINT64)(UINT_PTR)(size >= 0 ? packet->data : NULL), size, size >= 0 ? packet->flags : 0,
            size >= 0 ? packet->pts : 0, size >= 0 ? packet->dts : 0, size >= 0 ? packet->duration : 0, AVERROR_EXTERNAL, 0};
        if (unix_call(VT_UNIX_SEND, &p)) status = p.status;
        ReleaseSRWLockExclusive(&decoder_lock);
        return status;
    }
    VtCommand cmd = {.op = VT_SEND, .size = size};
    if (transfer(&cmd, sizeof(cmd), 1)) {
        if (size < 0) {
            transfer(&status, sizeof(status), 0);
        } else {
            VtPacket info = {packet->pts, packet->dts, packet->duration, packet->flags, 0};
            if (transfer(&info, sizeof(info), 1) && transfer(packet->data, size, 1))
                transfer(&status, sizeof(status), 0);
        }
    }
    ReleaseSRWLockExclusive(&decoder_lock);
    return status;
}

static void release_slot(void *opaque, uint8_t *data)
{
    (void)data;
    InterlockedExchange((volatile LONG *)opaque, 0);
}

static int restore_side(AVFrame *frame, enum AVFrameSideDataType type, const uint8_t *data, uint32_t size)
{
    if (!size) return 1;
    AVFrameSideData *side = side_new(frame, type, size);
    if (!side) return 0;
    memcpy(side->data, data, size);
    return 1;
}

static void release_unix(void *opaque, uint8_t *data)
{
    (void)data;
    VtUnixRelease p = {(UINT64)(UINT_PTR)opaque};
    unix_call(VT_UNIX_RELEASE, &p);
}

static int fill_frame(AVCodecContext *ctx, AVFrame *frame, const VtFrame *src);

static int receive_unix(AVCodecContext *ctx, AVFrame *frame)
{
    frame_unref(frame);
    VtUnixReceive p;
    memset(&p, 0, sizeof(p));
    if (!unix_call(VT_UNIX_RECEIVE, &p)) return AVERROR_EXTERNAL;
    if (p.status) return p.status;
    const VtFrame *f = &p.f;
    void *held = (void *)(UINT_PTR)p.held;
    if ((f->format != VT_NV12 && f->format != VT_P010) || f->width <= 0 || f->height <= 0 ||
        ((f->width | f->height) & 1) || !p.plane[0] || !p.plane[1] ||
        p.pitch[0] < f->width * (f->format == VT_P010 ? 2 : 1) || p.pitch[1] < p.pitch[0] / 2 ||
        f->light_size > sizeof(f->light)) {
        release_unix(held, NULL);
        return AVERROR_INVALIDDATA;
    }
    // LAV legge i due piani direttamente dalla memoria di VideoToolbox; al rilascio del frame torna alla libreria.
    frame->buf[0] = buffer_create((uint8_t *)(UINT_PTR)p.plane[0], (size_t)p.plane_bytes[0], release_unix, held,
        AV_BUFFER_FLAG_READONLY);
    if (!frame->buf[0]) {
        release_unix(held, NULL);
        return AVERROR(ENOMEM);
    }
    frame->data[0] = (uint8_t *)(UINT_PTR)p.plane[0];
    frame->data[1] = (uint8_t *)(UINT_PTR)p.plane[1];
    frame->linesize[0] = p.pitch[0];
    frame->linesize[1] = p.pitch[1];
    return fill_frame(ctx, frame, f);
}

static int receive_hardware(AVCodecContext *ctx, AVFrame *frame)
{
    if (unix_funcs) return receive_unix(ctx, frame);
    frame_unref(frame);
    int ret = request(VT_RECEIVE);
    if (ret < 0) return ret;
    VtFrame f;
    if (!transfer(&f, sizeof(f), 0) || f.slot < 0 || f.slot >= VT_SLOTS)
        return AVERROR_EXTERNAL;
    volatile LONG *state = (volatile LONG *)mapping + f.slot;
    if (InterlockedCompareExchange(state, 2, 2) != 2) return AVERROR_EXTERNAL;
    if ((f.format != VT_NV12 && f.format != VT_P010) || f.width <= 0 || f.height <= 0 ||
        f.width > VT_MAX_WIDTH || f.height > VT_MAX_HEIGHT || ((f.width | f.height) & 1) ||
        f.stride < f.width * (f.format == VT_P010 ? 2 : 1) ||
        (uint64_t)f.stride * (f.height + f.height/2) > VT_SLOT_BYTES ||
        f.light_size > sizeof(f.light)) {
        InterlockedExchange(state, 0);
        return AVERROR_INVALIDDATA;
    }
    uint8_t *data = vt_slot(mapping, f.slot);
    // LAV mantiene il riferimento al frame. Il buffer torna al produttore al rilascio.
    frame->buf[0] = buffer_create(data, (size_t)f.stride * (f.height + f.height/2), release_slot,
        (void *)state, AV_BUFFER_FLAG_READONLY);
    if (!frame->buf[0]) {
        InterlockedExchange(state, 0);
        return AVERROR(ENOMEM);
    }
    frame->data[0] = data;
    frame->data[1] = data + (size_t)f.stride * f.height;
    frame->linesize[0] = frame->linesize[1] = f.stride;
    return fill_frame(ctx, frame, &f);
}

static int fill_frame(AVCodecContext *ctx, AVFrame *frame, const VtFrame *src)
{
    VtFrame f = *src;
    frame->extended_data = frame->data;
    ctx->pix_fmt = frame->format = f.format == VT_P010 ? AV_PIX_FMT_P010LE : AV_PIX_FMT_NV12;
    ctx->width = frame->width = f.width;
    ctx->height = frame->height = f.height;
    frame->pts = f.pts;
    frame->pkt_dts = f.dts;
    frame->best_effort_timestamp = f.best;
    frame->duration = f.duration;
    frame->sample_aspect_ratio = (AVRational){f.sar_num, f.sar_den};
    frame->pict_type = f.picture;
    frame->flags = f.flags;
    frame->repeat_pict = f.repeat;
    ctx->color_range = frame->color_range = f.range;
    ctx->color_primaries = frame->color_primaries = f.primaries;
    ctx->color_trc = frame->color_trc = f.transfer;
    ctx->colorspace = frame->colorspace = f.colorspace;
    ctx->chroma_sample_location = frame->chroma_location = f.chroma;
    ctx->progressive_sequence = !(f.flags & AV_FRAME_FLAG_INTERLACED);
    int mastering_ok = 1;
    if (f.has_mastering) {
        AVMasteringDisplayMetadata m = {0};
        memcpy(m.display_primaries, f.mastering.primaries, sizeof(m.display_primaries));
        memcpy(m.white_point, f.mastering.white_point, sizeof(m.white_point));
        memcpy(&m.min_luminance, f.mastering.min_luminance, sizeof(m.min_luminance));
        memcpy(&m.max_luminance, f.mastering.max_luminance, sizeof(m.max_luminance));
        m.has_primaries = f.mastering.has_primaries;
        m.has_luminance = f.mastering.has_luminance;
        mastering_ok = restore_side(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA, (const uint8_t *)&m, sizeof(m));
    } else {
        for (int i = 0; i < ctx->nb_coded_side_data; i++) {
            const AVPacketSideData *side = &ctx->coded_side_data[i];
            if (side->type == AV_PKT_DATA_MASTERING_DISPLAY_METADATA && side->size == sizeof(AVMasteringDisplayMetadata)) {
                mastering_ok = restore_side(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA, side->data, (uint32_t)side->size);
                break;
            }
        }
    }
    if (!mastering_ok || !restore_side(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL, f.light, f.light_size)) {
        frame_unref(frame);
        return AVERROR(ENOMEM);
    }
    InterlockedExchange(&hardware_frames, 1);
    return 0;
}

static int receive_hook(AVCodecContext *ctx, AVFrame *frame)
{
    AcquireSRWLockExclusive(&decoder_lock);
    if (ctx != hardware || !hardware) {
        ReleaseSRWLockExclusive(&decoder_lock);
        return receive_original(ctx, frame);
    }
    int ret = receive_hardware(ctx, frame);
    ReleaseSRWLockExclusive(&decoder_lock);
    return ret;
}

static void flush_hook(AVCodecContext *ctx)
{
    AcquireSRWLockExclusive(&decoder_lock);
    if (hardware && ctx == hardware) { if (unix_funcs) unix_call(VT_UNIX_FLUSH, NULL); else request(VT_FLUSH); }
    ReleaseSRWLockExclusive(&decoder_lock);
    flush_original(ctx);
}

static void free_hook(AVCodecContext **ctx)
{
    AcquireSRWLockExclusive(&decoder_lock);
    if (ctx && hardware && *ctx == hardware) {
        if (unix_funcs) unix_call(VT_UNIX_CLOSE, NULL); else request(VT_CLOSE);
        hardware = NULL;
        InterlockedExchange(&hardware_frames, 0);
    }
    ReleaseSRWLockExclusive(&decoder_lock);
    free_original(ctx);
}

static void **find_named_thunk(uint8_t *base, DWORD lookup, DWORD iat, const char *name)
{
    if (!lookup || !iat) return NULL;
    const IMAGE_THUNK_DATA64 *names = (const IMAGE_THUNK_DATA64 *)(base + lookup);
    void **entries = (void **)(base + iat);
    for (size_t i = 0; names[i].u1.AddressOfData; i++) {
        if (IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal)) continue;
        const IMAGE_IMPORT_BY_NAME *import = (const IMAGE_IMPORT_BY_NAME *)(base + names[i].u1.AddressOfData);
        if (!strcmp((const char *)import->Name, name)) return &entries[i];
    }
    return NULL;
}

static void **find_import(HMODULE module, const char *dll, const char *name)
{
    uint8_t *base = (uint8_t *)module;
    const IMAGE_DOS_HEADER *dos = (const IMAGE_DOS_HEADER *)base;
    const IMAGE_NT_HEADERS64 *nt = (const IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (rva) {
        for (const IMAGE_IMPORT_DESCRIPTOR *d = (const IMAGE_IMPORT_DESCRIPTOR *)(base + rva); d->Name; d++)
            if (!_stricmp((const char *)(base + d->Name), dll))
                return find_named_thunk(base, d->OriginalFirstThunk, d->FirstThunk, name);
    }
    rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT].VirtualAddress;
    // The x64 delay-load table uses RVAs. Fields are flags, name, HMODULE, IAT, INT, BIAT, UIAT, time.
    if (rva) {
        for (const DWORD *d = (const DWORD *)(base + rva); d[1]; d += 8)
            if ((d[0] & 1) && !_stricmp((const char *)(base + d[1]), dll))
                return find_named_thunk(base, d[4], d[3], name);
    }
    return NULL;
}

static HMODULE load_adjacent(HMODULE source, const wchar_t *name)
{
    wchar_t path[MAX_PATH * 2];
    DWORD size = GetModuleFileNameW(source, path, sizeof(path)/sizeof(*path));
    if (!size || size >= sizeof(path)/sizeof(*path)) return NULL;
    wchar_t *leaf = wcsrchr(path, L'\\');
    if (!leaf || (size_t)(leaf - path + 1) + wcslen(name) >= sizeof(path)/sizeof(*path)) return NULL;
    wcscpy(leaf + 1, name);
    return LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
}

__declspec(dllexport) int cinecore_vt_active(void)
{
    if (InterlockedCompareExchange(&transport_failed, 0, 0)) return -1;
    return InterlockedCompareExchange(&hardware_frames, 0, 0) != 0;
}

__declspec(dllexport) int cinecore_vt_install(void)
{
    HMODULE lav = GetModuleHandleW(L"LAVVideo.ax");
    const char *codec_name = "avcodec-lav-62.dll";
    HMODULE util = lav ? load_adjacent(lav, L"avutil-lav-60.dll") : NULL;
    HMODULE codec = util ? load_adjacent(lav, L"avcodec-lav-62.dll") : NULL;
    if (!lav || !codec || !util) return 0;
    unsigned (*codec_version)(void) = (void *)GetProcAddress(codec, "avcodec_version");
    unsigned (*util_version)(void) = (void *)GetProcAddress(util, "avutil_version");
    if (!codec_version || !util_version || codec_version() != LIBAVCODEC_VERSION_INT ||
        util_version() != LIBAVUTIL_VERSION_INT) return 0;
    buffer_create = (void *)GetProcAddress(util, "av_buffer_create");
    side_new = (void *)GetProcAddress(util, "av_frame_new_side_data");
    frame_unref = (void *)GetProcAddress(util, "av_frame_unref");
    if (!buffer_create || !side_new || !frame_unref) return 0;
    struct {
        const char *name;
        void *hook;
        void **original;
        void **entry;
        void *saved;
    } imports[] = {
        {"avcodec_open2", open_hook, (void **)&open_original, NULL, NULL},
        {"avcodec_send_packet", send_hook, (void **)&send_original, NULL, NULL},
        {"avcodec_receive_frame", receive_hook, (void **)&receive_original, NULL, NULL},
        {"avcodec_flush_buffers", flush_hook, (void **)&flush_original, NULL, NULL},
        {"avcodec_free_context", free_hook, (void **)&free_original, NULL, NULL},
    };
    const int count = sizeof(imports) / sizeof(*imports);
    for (int i = 0; i < count; i++) {
        imports[i].entry = find_import(lav, codec_name, imports[i].name);
        if (!imports[i].entry) return 0;
        imports[i].saved = *imports[i].entry;
        *imports[i].original = (void *)GetProcAddress(codec, imports[i].name);
        if (!*imports[i].original) return 0;
    }
    for (int i = 0; i < count; i++) {
        DWORD protection, ignored;
        if (!VirtualProtect(imports[i].entry, sizeof(void *), PAGE_READWRITE, &protection)) {
            for (int j = 0; j < i; j++)
                if (VirtualProtect(imports[j].entry, sizeof(void *), PAGE_READWRITE, &protection)) {
                    *imports[j].entry = imports[j].saved;
                    VirtualProtect(imports[j].entry, sizeof(void *), protection, &ignored);
                }
            return 0;
        }
        *imports[i].entry = imports[i].hook;
        VirtualProtect(imports[i].entry, sizeof(void *), protection, &ignored);
    }
    return 1;
}
