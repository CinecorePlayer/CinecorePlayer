#ifndef CINECORE_VT_PROTOCOL_H
#define CINECORE_VT_PROTOCOL_H
#include <stddef.h>
#include <stdint.h>

enum { VT_OPEN = 1, VT_SEND, VT_RECEIVE, VT_FLUSH, VT_CLOSE };
enum { VT_NV12 = 1, VT_P010 };
#define VT_MAGIC 0x31545643u
#define VT_SLOTS 12
#define VT_MAX_WIDTH 4096
#define VT_MAX_HEIGHT 2304
#define VT_HEADER_BYTES 4096u
#define VT_SLOT_BYTES (VT_MAX_WIDTH * VT_MAX_HEIGHT * 3u)
#define VT_MAP_BYTES (VT_HEADER_BYTES + VT_SLOTS * VT_SLOT_BYTES)
#define VT_PACKET_LIMIT (16 * 1024 * 1024)
#define VT_EXTRA_LIMIT (1024 * 1024)
#define VT_TOKEN_BYTES 32

typedef struct {
    uint32_t magic;
    char token[VT_TOKEN_BYTES];
} VtHello;

typedef struct {
    int32_t op;
    int32_t size;
} VtCommand;

typedef struct {
    int32_t width, height, profile, level;
    int32_t range, primaries, transfer, colorspace, chroma;
    int32_t tb_num, tb_den;
} VtOpen;

typedef struct {
    int64_t pts, dts, duration;
    int32_t flags;
    int32_t reserved;
} VtPacket;

typedef struct {
    int32_t primaries[3][2][2], white_point[2][2];
    int32_t min_luminance[2], max_luminance[2];
    int32_t has_primaries, has_luminance;
} VtMasteringDisplay;

typedef struct {
    int64_t pts, dts, best, duration;
    int32_t slot, format, width, height, stride;
    int32_t sar_num, sar_den, picture, flags, repeat;
    int32_t range, primaries, transfer, colorspace, chroma;
    uint32_t has_mastering, light_size;
    VtMasteringDisplay mastering;
    uint8_t light[16];
} VtFrame;

static inline uint8_t *vt_slot(uint8_t *mapping, int slot)
{
    return mapping + VT_HEADER_BYTES + (size_t)slot * VT_SLOT_BYTES;
}
#endif
