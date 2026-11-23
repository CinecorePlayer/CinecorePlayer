// Interfaccia fra CinecoreVideoToolbox.dll (Windows, sotto Wine) e cinecorevt.so (macOS, nello stesso processo).
// Solo tipi a dimensione fissa: la stessa struttura e' vista da codice x86-64 (Windows) e arm64 (macOS).
#ifndef CINECORE_VT_UNIX_H
#define CINECORE_VT_UNIX_H
#include <stdint.h>
#include "vt_protocol.h"

enum { VT_UNIX_OPEN, VT_UNIX_SEND, VT_UNIX_RECEIVE, VT_UNIX_RELEASE, VT_UNIX_FLUSH, VT_UNIX_CLOSE };
enum { VT_UNIX_NV12 = VT_NV12, VT_UNIX_P010 = VT_P010 };
#define VT_UNIX_PACKET_LIMIT VT_PACKET_LIMIT
#define VT_UNIX_EXTRA_LIMIT VT_EXTRA_LIMIT

typedef struct {
    int32_t width, height, profile, level;
    int32_t range, primaries, transfer, colorspace, chroma;
    int32_t tb_num, tb_den;
    int32_t extra_size;
    uint64_t extra;          // puntatore ai dati extra (hvcC)
    int32_t status;          // uscita: codice FFmpeg (Windows)
    int32_t reserved;
} VtUnixOpen;

typedef struct {
    uint64_t data;           // puntatore al pacchetto
    int32_t size;            // -1 = fine flusso
    int32_t flags;
    int64_t pts, dts, duration;
    int32_t status;
    int32_t reserved;
} VtUnixSend;

typedef struct {
    VtFrame f;               // metadati (slot non usato)
    uint64_t plane[2];       // indirizzi dei due piani (memoria di VideoToolbox, sola lettura)
    uint64_t plane_bytes[2];
    int32_t pitch[2];
    uint64_t held;           // da passare a VT_UNIX_RELEASE quando LAV ha finito
    int32_t status;
    int32_t reserved;
} VtUnixReceive;

typedef struct { uint64_t held; } VtUnixRelease;
#endif
