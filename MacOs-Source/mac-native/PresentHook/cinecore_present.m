/*
 * Cinecore (macOS): exact-vsync Metal presentation for madVR under Wine.
 * Canonical copy: mac-native/PresentHook/cinecore_present.m in the Cinecore tree; it is compiled into wined3d.so
 * (dlls/wined3d/cinecore_present.m, see mac/wine-patches) because Wine's loader re-executes itself without
 * DYLD_INSERT_LIBRARIES and wined3d.so is always loaded in the video process.
 *
 * The D3D11->Metal layer (Mullion/DXMT) presents with -presentDrawable: (as soon as the GPU is done) or, with a
 * sync interval >= 1, with -presentDrawable:afterMinimumDuration:. Measured on a MacBook Air in Low Power Mode
 * (dist/debug-20261111/prestazioni/cadenza):
 *  - immediate presents reach the screen when the GPU finishes: with the GPU busy with madVR's shaders 10-17% of
 *    the frames miss their vsync, some are never shown;
 *  - scheduled presents (afterMinimumDuration, atTime) run on a 30 Hz grid: in Low Power Mode macOS halves the
 *    display link of the process (CVDisplayLink reports 33.3 ms on a 60 Hz panel).
 * Here the timed form is replaced: the command buffer is committed without presenting, so the GPU fills the
 * drawable ahead of time, and -[MTLDrawable present] is called by a timer thread right before the wanted vsync.
 * The "duration" that arrives is k / refresh rate and means "show at the k-th vsync from now" (k = sync interval
 * passed by the VideoHost, 1..4).
 * The module also publishes the real vsync clock (file CINECORE_VSYNC_FILE): the VideoHost uses it as its grid.
 * Active only when CINECORE_VSYNC_FILE is set.
 */

#if 0
#pragma makedep unix
#endif

#include "config.h"

#if !defined(SONAME_LIBVA) || !defined(SONAME_LIBVA_DRM)

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <QuartzCore/QuartzCore.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreGraphics/CoreGraphics.h>
#import <objc/runtime.h>
#import <mach/mach_time.h>
#import <mach/mach.h>
#import <mach/task_policy.h>
#import <sys/resource.h>
#import <os/lock.h>
#import <sys/mman.h>
#import <pthread.h>
#import <fcntl.h>
#import <unistd.h>
#import <string.h>
#import <stdatomic.h>
#import <dlfcn.h>

#pragma clang diagnostic ignored "-Wdeprecated-declarations"

typedef struct {
    volatile uint64_t seq;       /* odd while being written */
    int64_t  vsync_ns;           /* a real vsync, continuous clock (the one behind Wine's QueryPerformanceCounter) */
    double   period_ns;          /* real refresh period */
    uint64_t hooked;             /* presents scheduled by the hook */
    uint64_t magic;              /* 'CCVSYNC1' */
    int64_t  abs_minus_cont_ns;  /* diagnostics */
    uint64_t late;               /* GPU not done at the wanted time: presented late */
    uint64_t pid;
    /* Written by the VideoHost: absolute vsync (continuous clock, ns) at which each timed present must appear,
     * in call order. The hook consumes one entry per timed present. */
    volatile uint64_t target_count;
    int64_t  targets[16];
} vsync_page;

#define MAX_ITEMS 16
typedef struct { id drawable; uint64_t fire, hooked_at; int completed, used; } item;
static int g_debug; static double g_gpu_sum, g_gpu_max, g_margin_sum, g_margin_min = 1e9, g_lead_sum; static int g_dn, g_bins[8]; static int g_off[7], g_pn; static double g_frac_sum, g_frac_abs, g_last_pt; static char g_seq[128];

static vsync_page *g_page;
static os_unfair_lock g_lock = OS_UNFAIR_LOCK_INIT;
static double g_v0, g_period = 1.0 / 60.0;   /* seconds, host clock (CACurrentMediaTime) */
static mach_timebase_info_data_t g_tb;
static void (*g_orig)(id, SEL, id, CFTimeInterval);
static _Atomic int g_started;
static double g_guard = 0.5, g_nominal_hz;
static id g_activity;
static uint64_t g_target_read; static int64_t g_cont_minus_abs_ns;
/* Lo schermo del film: il display link e la frequenza nominale seguono la finestra (prima: sempre lo schermo
 * principale e la frequenza letta una volta all'avvio, cosi' con il proiettore a 23,976 Hz la griglia dei vsync era
 * quella di 60 Hz "corretta" per un fattore sbagliato). */
static CVDisplayLinkRef g_link;
static _Atomic uint32_t g_display;
static double g_last_display_check;
static _Atomic int g_display_pending;
static item g_items[MAX_ITEMS];
static pthread_mutex_t g_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_cond = PTHREAD_COND_INITIALIZER;

static double host_seconds(uint64_t t) { return (double)t * g_tb.numer / g_tb.denom / 1e9; }
static uint64_t host_ticks(double s) { return (uint64_t)(s * 1e9 * g_tb.denom / g_tb.numer); }

static CVReturn link_cb(CVDisplayLinkRef link, const CVTimeStamp *now, const CVTimeStamp *out, CVOptionFlags f, CVOptionFlags *fo, void *ctx)
{
    double p = CVDisplayLinkGetActualOutputVideoRefreshPeriod(link), v;
    if (!(p > 0.002 && p < 0.1)) { CVTime n = CVDisplayLinkGetNominalOutputVideoRefreshPeriod(link); p = n.timeScale ? (double)n.timeValue / n.timeScale : 1.0 / 60.0; }
    /* Low Power Mode halves the display link (30 Hz on a 60 Hz panel): the real period comes from the display
     * mode, the display link only gives the phase. */
    if (g_nominal_hz > 0) { double r = round(p * g_nominal_hz); if (r >= 2 && r <= 4) p /= r; }
    v = host_seconds(out->hostTime);
    os_unfair_lock_lock(&g_lock); g_v0 = v; g_period = p; os_unfair_lock_unlock(&g_lock);
    if (g_page)
    {
        int64_t d = (int64_t)((double)mach_continuous_time() * g_tb.numer / g_tb.denom) - (int64_t)((double)mach_absolute_time() * g_tb.numer / g_tb.denom);
        g_page->seq++;
        g_cont_minus_abs_ns = d;
        g_page->vsync_ns = (int64_t)(v * 1e9) + d; g_page->period_ns = p * 1e9; g_page->abs_minus_cont_ns = -d;
        g_page->seq++;
    }
    return kCVReturnSuccess;
}

/* Timer thread: flips each drawable right before its vsync, if the GPU has finished it. */
static void *timer_main(void *arg)
{
    pthread_setname_np("cinecore-present");
    pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
    pthread_mutex_lock(&g_mutex);
    for (;;)
    {
        int best = -1, i;
        for (i = 0; i < MAX_ITEMS; i++) if (g_items[i].used && g_items[i].completed && (best < 0 || g_items[i].fire < g_items[best].fire)) best = i;
        if (best < 0) { pthread_cond_wait(&g_cond, &g_mutex); continue; }
        uint64_t fire = g_items[best].fire;
        if (mach_absolute_time() < fire)
        {
            pthread_mutex_unlock(&g_mutex);
            mach_wait_until(fire);
            pthread_mutex_lock(&g_mutex);
            continue; /* look again: an earlier one may have completed meanwhile */
        }
        id d = g_items[best].drawable;
        g_items[best].used = 0; g_items[best].drawable = nil;
        pthread_mutex_unlock(&g_mutex);
        [(id<MTLDrawable>)d present];
        [d release];
        pthread_mutex_lock(&g_mutex);
    }
    return NULL;
}

/* Ogni mezzo secondo, sul thread principale (AppKit): schermo della finestra del drawable e sua frequenza vera. */
static void track_display(id drawable)
{
    double now = CACurrentMediaTime();
    id layer;
    if (now - g_last_display_check < 0.5 || atomic_load(&g_display_pending)) return;
    g_last_display_check = now;
    if (![drawable respondsToSelector:@selector(layer)]) return;
    layer = [[drawable layer] retain];
    if (!layer) return;
    atomic_store(&g_display_pending, 1);
    dispatch_async(dispatch_get_main_queue(), ^{
        Class viewClass = NSClassFromString(@"NSView");
        id view = nil, l = layer, window, screen;
        uint32_t display = atomic_load(&g_display), found = 0;
        int depth;
        /* La vista proprietaria del layer (o di uno dei suoi contenitori). */
        for (depth = 0; l && depth < 6 && !view; depth++)
        {
            id del = [l respondsToSelector:@selector(delegate)] ? [l delegate] : nil;
            if (del && viewClass && [del isKindOfClass:viewClass]) view = del;
            else l = [l respondsToSelector:@selector(superlayer)] ? [l superlayer] : nil;
        }
        window = view ? [view window] : nil;
        screen = window ? [window screen] : nil;
        if (screen)
        {
            id number = [[screen deviceDescription] objectForKey:@"NSScreenNumber"];
            if (number) found = (uint32_t)[number unsignedIntValue];
        }
        { static int once; if (!once) { once = 1; fprintf(stderr, "cinecore-present: schermo della finestra del film %u (vista %s)\n", found, view ? "trovata" : "non trovata"); } }
        if (found && found != display && g_link)
        {
            if (CVDisplayLinkSetCurrentCGDisplay(g_link, found) == kCVReturnSuccess)
            {
                fprintf(stderr, "cinecore-present: display link sullo schermo %u (prima %u)\n", found, display);
                atomic_store(&g_display, found);
                display = found;
            }
        }
        {
            CGDisplayModeRef mode = CGDisplayCopyDisplayMode(display ? display : CGMainDisplayID());
            if (mode)
            {
                double hz = CGDisplayModeGetRefreshRate(mode);
                CGDisplayModeRelease(mode);
                if (hz >= 20 && hz <= 480 && fabs(hz - g_nominal_hz) > 0.01)
                {
                    fprintf(stderr, "cinecore-present: frequenza dello schermo %u: %.3f Hz (prima %.3f)\n", display, hz, g_nominal_hz);
                    g_nominal_hz = hz;
                }
            }
        }
        [layer release];
        atomic_store(&g_display_pending, 0);
    });
}

static void hook_present(id self, SEL _cmd, id drawable, CFTimeInterval duration)
{
    double v0, p, now, n, target; long k; int slot = -1, i;
    track_display(drawable);
    os_unfair_lock_lock(&g_lock); v0 = g_v0; p = g_period; os_unfair_lock_unlock(&g_lock);
    if (v0 == 0 || !(duration > 0)) { g_orig(self, _cmd, drawable, duration); return; }
    /* duration = k / nominal refresh rate (that is how the D3D11 layer computes it): k = vsyncs from now. */
    k = lround(duration * round(g_nominal_hz));
    if (k < 1) k = 1;
    if (k > 8) k = 8;
    now = CACurrentMediaTime();
    n = floor((now - v0) / p);
    target = v0 + (n + k) * p;
    /* Preferred: the absolute vsync published by the VideoHost for this present (the call reaches this point with
     * a variable delay, so "k vsyncs from now" can be off by one). */
    if (g_page)
    {
        uint64_t r = g_target_read;
        if (r < g_page->target_count)
        {
            double t = (double)(g_page->targets[r % 16] - g_cont_minus_abs_ns) / 1e9;
            g_target_read = r + 1;
            if (g_page->target_count - g_target_read > 8) g_target_read = g_page->target_count; /* out of step: resync */
            if (t > now - 0.1 && t < now + 0.3) target = t;
        }
    }
    pthread_mutex_lock(&g_mutex);
    for (i = 0; i < MAX_ITEMS; i++) if (!g_items[i].used) { slot = i; break; }
    if (slot >= 0)
    {
        g_items[slot].drawable = [drawable retain];
        g_items[slot].fire = host_ticks(target - g_guard * p);
        g_items[slot].completed = 0; g_items[slot].used = 1; g_items[slot].hooked_at = mach_absolute_time();
    }
    pthread_mutex_unlock(&g_mutex);
    if (slot < 0) { [(id<MTLCommandBuffer>)self presentDrawable:drawable]; return; }
    if (g_debug)
    {
        [(id<MTLDrawable>)drawable addPresentedHandler:^(id<MTLDrawable> d) {
            double pt = d.presentedTime, off, fr; int o;
            if (pt <= 0) return;
            off = (pt - target) / p; o = (int)lround(off); fr = off - o;
            pthread_mutex_lock(&g_mutex);
            g_off[o < -2 ? 0 : o > 3 ? 6 : o + 2]++; g_frac_sum += fr; g_frac_abs += fabs(fr);
            { long dd = g_last_pt > 0 ? lround((pt - g_last_pt) / p) : 0; g_seq[g_pn] = dd < 0 ? '-' : dd > 9 ? '+' : (char)('0' + dd); g_last_pt = pt; }
            if (++g_pn >= 120)
            {
                g_seq[120] = 0; fprintf(stderr, "cinecore-present: durate a schermo %s\n", g_seq);
                fprintf(stderr, "cinecore-present: a schermo rispetto al vsync voluto: <=-2:%d -1:%d 0:%d +1:%d +2:%d +3:%d oltre:%d | scarto di fase medio %.3f (assoluto %.3f) vsync\n",
                        g_off[0], g_off[1], g_off[2], g_off[3], g_off[4], g_off[5], g_off[6], g_frac_sum / g_pn, g_frac_abs / g_pn);
                g_pn = 0; g_frac_sum = g_frac_abs = 0; memset(g_off, 0, sizeof g_off);
            }
            pthread_mutex_unlock(&g_mutex);
        }];
    }
    if (g_page) g_page->hooked++;
    [(id<MTLCommandBuffer>)self addCompletedHandler:^(id<MTLCommandBuffer> cb) {
        pthread_mutex_lock(&g_mutex);
        uint64_t t = mach_absolute_time();
        g_items[slot].completed = 1;
        if (g_page && t > g_items[slot].fire) g_page->late++;
        if (g_debug)
        {
            double gpu = host_seconds(t - g_items[slot].hooked_at) * 1e3, margin = (host_seconds(g_items[slot].fire) - host_seconds(t)) * 1e3;
            int b = gpu < 2 ? 0 : gpu < 5 ? 1 : gpu < 10 ? 2 : gpu < 15 ? 3 : gpu < 20 ? 4 : gpu < 30 ? 5 : gpu < 45 ? 6 : 7;
            g_bins[b]++; g_gpu_sum += gpu; if (gpu > g_gpu_max) g_gpu_max = gpu; g_margin_sum += margin; if (margin < g_margin_min) g_margin_min = margin;
            g_lead_sum += (host_seconds(g_items[slot].fire) - host_seconds(g_items[slot].hooked_at)) * 1e3;
            if (++g_dn >= 120)
            {
                fprintf(stderr, "cinecore-present: gancio->GPU finita media %.1f max %.1f ms | <2:%d <5:%d <10:%d <15:%d <20:%d <30:%d <45:%d oltre:%d | gancio->giro media %.1f ms, margine medio %.1f min %.1f ms\n",
                        g_gpu_sum / g_dn, g_gpu_max, g_bins[0], g_bins[1], g_bins[2], g_bins[3], g_bins[4], g_bins[5], g_bins[6], g_bins[7], g_lead_sum / g_dn, g_margin_sum / g_dn, g_margin_min);
                g_dn = 0; g_gpu_sum = g_gpu_max = g_margin_sum = g_lead_sum = 0; g_margin_min = 1e9; memset(g_bins, 0, sizeof g_bins);
            }
        }
        pthread_cond_signal(&g_cond);
        pthread_mutex_unlock(&g_mutex);
    }];
}

static void (*g_orig_plain)(id, SEL, id);
static void hook_plain(id self, SEL _cmd, id drawable);
static void start(void)
{
    const char *path = getenv("CINECORE_VSYNC_FILE"), *g;
    CGDisplayModeRef mode; CVDisplayLinkRef link = NULL; pthread_t th; int fd;
    if (atomic_exchange(&g_started, 1)) return;
    if (!path || !*path) return;
    if ((g = getenv("CINECORE_PRESENT_GUARD"))) { double x = atof(g); if (x > 0 && x < 2) g_guard = x; }
    g_debug = getenv("CINECORE_PRESENT_DEBUG") != NULL;
    mach_timebase_info(&g_tb);
    fd = open(path, O_RDWR | O_CREAT, 0600);
    if (fd >= 0)
    {
        if (ftruncate(fd, sizeof(vsync_page)) == 0)
        {
            void *m = mmap(NULL, sizeof(vsync_page), PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
            if (m != MAP_FAILED) { g_page = m; memset(g_page, 0, sizeof *g_page); g_page->magic = 0x31434e5953564343ULL; g_page->pid = (uint64_t)getpid(); }
        }
        close(fd);
    }
    /* The video process must not be throttled (App Nap, timer coalescing, reduced frame rate in Low Power Mode). */
    if (!getenv("CINECORE_PRESENT_NO_ACTIVITY"))
        g_activity = [[[NSProcessInfo processInfo] beginActivityWithOptions:(NSActivityUserInitiated | NSActivityLatencyCritical)
                                                                     reason:@"Cinecore video playback"] retain];
    if (g_debug)
    {
        struct task_category_policy cat = {0}; mach_msg_type_number_t cnt = TASK_CATEGORY_POLICY_COUNT; boolean_t def = 0;
        struct task_qos_policy qos = {0}; mach_msg_type_number_t qc = TASK_QOS_POLICY_COUNT; boolean_t qd = 0;
        kern_return_t kr = task_policy_get(mach_task_self(), TASK_CATEGORY_POLICY, (task_policy_t)&cat, &cnt, &def);
        kern_return_t kq = task_policy_get(mach_task_self(), TASK_BASE_QOS_POLICY, (task_policy_t)&qos, &qc, &qd);
        fprintf(stderr, "cinecore-present: ruolo del processo %d (kr=%d), qos latenza=0x%x throughput=0x%x (kr=%d), priorita' %d, darwinbg %d, risparmio energetico %d\n",
                cat.role, kr, qos.task_latency_qos_tier, qos.task_throughput_qos_tier, kq, getpriority(PRIO_PROCESS, 0), getpriority(PRIO_DARWIN_PROCESS, 0),
                (int)[[NSProcessInfo processInfo] isLowPowerModeEnabled]);
    }
    /* Schermo di partenza: quello indicato dall'app (CINECORE_DISPLAY_ID), altrimenti il principale; poi lo segue
     * track_display. */
    { const char *d = getenv("CINECORE_DISPLAY_ID"); uint32_t id0 = d ? (uint32_t)strtoul(d, NULL, 10) : 0; atomic_store(&g_display, id0 ? id0 : CGMainDisplayID()); }
    mode = CGDisplayCopyDisplayMode(atomic_load(&g_display));
    if (mode) { g_nominal_hz = CGDisplayModeGetRefreshRate(mode); CGDisplayModeRelease(mode); }
    if (!(g_nominal_hz >= 20 && g_nominal_hz <= 480)) g_nominal_hz = 60;
    if (CVDisplayLinkCreateWithCGDisplay(atomic_load(&g_display), &link) != kCVReturnSuccess || !link)
    {
        atomic_store(&g_display, CGMainDisplayID());
        if (CVDisplayLinkCreateWithCGDisplay(CGMainDisplayID(), &link) != kCVReturnSuccess || !link) { fprintf(stderr, "cinecore-present: nessun display link\n"); return; }
    }
    g_link = link;
    CVDisplayLinkSetOutputCallback(link, link_cb, NULL);
    CVDisplayLinkStart(link);
    pthread_create(&th, NULL, timer_main, NULL);
    pthread_detach(th);
    @autoreleasepool
    {
        id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
        id<MTLCommandQueue> q = [dev newCommandQueue];
        id<MTLCommandBuffer> cb = [q commandBuffer];
        Method m = class_getInstanceMethod(object_getClass(cb), @selector(presentDrawable:afterMinimumDuration:));
        if (m)
        {
            g_orig = (void (*)(id, SEL, id, CFTimeInterval))method_getImplementation(m);
            method_setImplementation(m, (IMP)hook_present);
            fprintf(stderr, "cinecore-present: gancio attivo (pid %d, %.3f Hz, anticipo %.2f)\n", getpid(), g_nominal_hz, g_guard);
        }
        else fprintf(stderr, "cinecore-present: metodo di presentazione non trovato\n");
        if (g_debug)
        {
            Method mp = class_getInstanceMethod(object_getClass(cb), @selector(presentDrawable:));
            if (mp) { g_orig_plain = (void (*)(id, SEL, id))method_getImplementation(mp); method_setImplementation(mp, (IMP)hook_plain); }
        }
        [q release]; [dev release];
    }
}


/* ---------------------------------------------------------------------------------------------------------------
 * Diagnostics (CINECORE_PASS_CENSUS=1): what really reaches Metal from this process (wined3d's OpenGL runs on Metal
 * in-process, DXMT too). Every 3 s prints, per kind of render pass / texture copy: size, pixel format, load and
 * store action, count per second and megabytes per second the GPU has to read back (load) and write.
 * A render pass that fully overwrites its target but uses loadAction=Load makes a tile-based GPU read the whole
 * texture from memory first: pure waste, and in Low Power Mode memory traffic lowers the GPU frequency. */
typedef struct { int kind, w, h, fmt, load, store; char q[20]; long n; } pass_row;
static pass_row g_rows[96]; static int g_nrows; static os_unfair_lock g_plock = OS_UNFAIR_LOCK_INIT;
static id (*g_orig_rce)(id, SEL, id);
static void (*g_orig_copy_full)(id, SEL, id, NSUInteger, NSUInteger, MTLOrigin, MTLSize, id, NSUInteger, NSUInteger, MTLOrigin);
static void (*g_orig_copy_tex)(id, SEL, id, id);
static int fmt_bytes(int f)
{
    switch (f) { case 10: case 11: case 12: case 13: case 14: return 1; case 20: case 22: case 23: case 24: case 25: case 30: case 31: case 32: case 33: case 34: return 2;
                 case 110: case 112: case 113: case 114: case 115: return 8; case 123: case 124: case 125: return 16; case 250: case 252: case 253: case 255: case 260: return 4; default: return 4; }
}
static void pass_note(int kind, id tex, int load, int store, int w, int h, const void *ra)
{
    int fmt = tex ? (int)[(id<MTLTexture>)tex pixelFormat] : 0, i; char q[20] = "";
    if (tex && w == 0) { w = (int)[(id<MTLTexture>)tex width]; h = (int)[(id<MTLTexture>)tex height]; }
    /* who asked: the library of the caller (AppleMetalOpenGLRenderer = wined3d's OpenGL, winemetal.so = DXMT) */
    { Dl_info di; if (ra && dladdr(ra, &di) && di.dli_fname) { const char *b = strrchr(di.dli_fname, '/'); strlcpy(q, b ? b + 1 : di.dli_fname, sizeof q); } }
    os_unfair_lock_lock(&g_plock);
    for (i = 0; i < g_nrows; i++) if (g_rows[i].kind == kind && g_rows[i].w == w && g_rows[i].h == h && g_rows[i].fmt == fmt && g_rows[i].load == load && g_rows[i].store == store && !strcmp(g_rows[i].q, q)) break;
    if (i == g_nrows && g_nrows < 96) { g_rows[i] = (pass_row){ kind, w, h, fmt, load, store }; strlcpy(g_rows[i].q, q, sizeof g_rows[i].q); g_nrows++; }
    if (i < g_nrows) g_rows[i].n++;
    os_unfair_lock_unlock(&g_plock);
}
static id hook_rce(id self, SEL _cmd, id desc)
{
    MTLRenderPassDescriptor *d = desc; int k;
    for (k = 0; k < 4; k++)
    {
        MTLRenderPassColorAttachmentDescriptor *c = d.colorAttachments[k];
        if (c.texture) pass_note(0, c.texture, (int)c.loadAction, (int)c.storeAction, 0, 0, __builtin_return_address(0));
    }
    if (d.depthAttachment.texture) pass_note(1, d.depthAttachment.texture, (int)d.depthAttachment.loadAction, (int)d.depthAttachment.storeAction, 0, 0, __builtin_return_address(0));
    return g_orig_rce(self, _cmd, desc);
}
static void hook_copy_full(id self, SEL _cmd, id src, NSUInteger ss, NSUInteger sl, MTLOrigin so, MTLSize sz, id dst, NSUInteger ds, NSUInteger dl, MTLOrigin dorg)
{
    pass_note(2, src, (int)[(id<MTLTexture>)dst pixelFormat], 0, (int)sz.width, (int)sz.height, __builtin_return_address(0));
    g_orig_copy_full(self, _cmd, src, ss, sl, so, sz, dst, ds, dl, dorg);
}
static void hook_copy_tex(id self, SEL _cmd, id src, id dst)
{
    pass_note(2, src, (int)[(id<MTLTexture>)dst pixelFormat], 0, 0, 0, __builtin_return_address(0));
    g_orig_copy_tex(self, _cmd, src, dst);
}
static int row_cmp(const void *a, const void *b)
{
    const pass_row *x = a, *y = b; double bx = (double)x->w * x->h * fmt_bytes(x->fmt) * x->n, by = (double)y->w * y->h * fmt_bytes(y->fmt) * y->n;
    return bx < by ? 1 : bx > by ? -1 : 0;
}
static void *census_main(void *arg)
{
    static const char *kinds[] = { "passata", "profondita", "copia" }, *loads[] = { "nonImporta", "RILEGGE", "pulisce" }, *stores[] = { "scarta", "salva", "risolve", "salva+risolve", "?", "?" };
    for (;;)
    {
        pass_row rows[96]; int n, i; double rd = 0, wr = 0, waste = 0; char dstf[12];
        sleep(3);
        os_unfair_lock_lock(&g_plock); n = g_nrows; memcpy(rows, g_rows, sizeof(pass_row) * n); for (i = 0; i < g_nrows; i++) g_rows[i].n = 0; os_unfair_lock_unlock(&g_plock);
        qsort(rows, n, sizeof(pass_row), row_cmp);
        for (i = 0; i < n; i++)
        {
            double mb = (double)rows[i].w * rows[i].h * fmt_bytes(rows[i].fmt) * rows[i].n / 3.0 / 1048576.0;
            if (!rows[i].n) continue;
            if (rows[i].kind == 2) { rd += mb; wr += mb; } else { if (rows[i].load == 1) { rd += mb; waste += mb; } if (rows[i].store == 1 || rows[i].store == 3) wr += mb; }
            if (i < 22) fprintf(stderr, "cinecore-census: %-10s %5dx%-5d fmt=%-3d %-10s %-6s %6.1f/s %7.0f MB/s %s\n", kinds[rows[i].kind], rows[i].w, rows[i].h, rows[i].fmt,
                                rows[i].kind == 2 ? "->" : loads[rows[i].load < 3 ? rows[i].load : 0], rows[i].kind == 2 ? (snprintf(dstf, sizeof dstf, "f%d", rows[i].load), dstf) : stores[rows[i].store < 6 ? rows[i].store : 4], rows[i].n / 3.0, mb, rows[i].q);
        }
        fprintf(stderr, "cinecore-census: TOTALE riletture per caricamento %.0f MB/s, letture (caricamenti+copie) %.0f MB/s, scritture %.0f MB/s\n", waste, rd, wr);
    }
    return NULL;
}
static void census_start(void)
{
    @autoreleasepool
    {
        id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
        id<MTLCommandQueue> q = [dev newCommandQueue];
        id<MTLCommandBuffer> cb = [q commandBuffer];
        id<MTLBlitCommandEncoder> be = [cb blitCommandEncoder];
        Method m = class_getInstanceMethod(object_getClass(cb), @selector(renderCommandEncoderWithDescriptor:)), m2, m3; pthread_t th;
        if (m) { g_orig_rce = (void *)method_getImplementation(m); method_setImplementation(m, (IMP)hook_rce); }
        m2 = class_getInstanceMethod(object_getClass(be), @selector(copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:));
        if (m2) { g_orig_copy_full = (void *)method_getImplementation(m2); method_setImplementation(m2, (IMP)hook_copy_full); }
        m3 = class_getInstanceMethod(object_getClass(be), @selector(copyFromTexture:toTexture:));
        if (m3) { g_orig_copy_tex = (void *)method_getImplementation(m3); method_setImplementation(m3, (IMP)hook_copy_tex); }
        [be endEncoding];
        fprintf(stderr, "cinecore-census: attivo (passate %d, copie %d/%d)\n", m != NULL, m2 != NULL, m3 != NULL);
        pthread_create(&th, NULL, census_main, NULL); pthread_detach(th);
        [q release]; [dev release];
    }
}

/* Diagnostics (CINECORE_PRESENT_DEBUG=1) for renderers that present without a duration (MPC-VR): where the frames
 * really land, as durations in vsyncs and as phase inside the refresh period (0 or 1 = on a vsync). */
static void hook_plain(id self, SEL _cmd, id drawable)
{
    [(id<MTLDrawable>)drawable addPresentedHandler:^(id<MTLDrawable> d) {
        static double last, ph_abs; static int n, bins[10]; static char seq[128];
        double pt = d.presentedTime, v0, p, ph; long dd;
        if (pt <= 0) return;
        os_unfair_lock_lock(&g_lock); v0 = g_v0; p = g_period; os_unfair_lock_unlock(&g_lock);
        pthread_mutex_lock(&g_mutex);
        ph = v0 > 0 ? (pt - v0) / p : 0; ph -= floor(ph);
        bins[(int)(ph * 10) % 10]++; ph_abs += ph > 0.5 ? 1 - ph : ph;
        dd = last > 0 ? lround((pt - last) / p) : 0; seq[n] = dd < 0 ? '-' : dd > 9 ? '+' : (char)('0' + dd); last = pt;
        if (++n >= 120)
        {
            seq[120] = 0;
            fprintf(stderr, "cinecore-present: (senza durata) durate a schermo %s\n", seq);
            fprintf(stderr, "cinecore-present: (senza durata) fase nel periodo, decimi: %d %d %d %d %d %d %d %d %d %d | distanza media dal vsync %.3f\n",
                    bins[0], bins[1], bins[2], bins[3], bins[4], bins[5], bins[6], bins[7], bins[8], bins[9], ph_abs / n);
            n = 0; ph_abs = 0; memset(bins, 0, sizeof bins);
        }
        pthread_mutex_unlock(&g_mutex);
    }];
    g_orig_plain(self, _cmd, drawable);
}

__attribute__((constructor)) static void cinecore_present_init(void)
{
    if (getenv("CINECORE_PASS_CENSUS")) dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{ census_start(); });
    if (!getenv("CINECORE_VSYNC_FILE")) return;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{ start(); });
}

#endif
