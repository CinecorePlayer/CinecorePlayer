#import <objc/runtime.h>
#import <AppKit/AppKit.h>
#import <QuartzCore/QuartzCore.h>

typedef void (*CinecoreVolumeChanged)(const char *path, int mounted);

void *CinecoreObserveVolumes(CinecoreVolumeChanged changed)
{
    NSNotificationCenter *center = NSWorkspace.sharedWorkspace.notificationCenter;
    id mounted = [center addObserverForName:NSWorkspaceDidMountNotification object:nil queue:NSOperationQueue.mainQueue
                               usingBlock:^(NSNotification *notification) {
        const char *path = [notification.userInfo[NSWorkspaceVolumeURLKey] fileSystemRepresentation];
        if (path) changed(path, 1);
    }];
    id unmounted = [center addObserverForName:NSWorkspaceDidUnmountNotification object:nil queue:NSOperationQueue.mainQueue
                                 usingBlock:^(NSNotification *notification) {
        const char *path = [notification.userInfo[NSWorkspaceVolumeURLKey] fileSystemRepresentation];
        if (path) changed(path, 0);
    }];
    return (__bridge_retained void *)@[mounted, unmounted];
}

void CinecoreStopObservingVolumes(void *handle)
{
    NSArray *observers = (__bridge_transfer NSArray *)handle;
    NSNotificationCenter *center = NSWorkspace.sharedWorkspace.notificationCenter;
    for (id observer in observers) [center removeObserver:observer];
}

#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000

API_AVAILABLE(macos(26.0))
@interface CinecoreGlassOverlay : NSView
@end

@implementation CinecoreGlassOverlay
// Avalonia continua a gestire mouse e tastiera nella vista sottostante.
- (NSView *)hitTest:(NSPoint)point { return nil; }
- (BOOL)isFlipped { return YES; }
@end

#endif

static char CinecoreGlassParentKey, CinecoreGlassChildKey;

void *CinecoreGlassCreate(void *handle)
{
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
    if (@available(macOS 26.0, *)) {
        NSWindow *window = (__bridge NSWindow *)handle;
        NSView *content = window.contentView;
        if (!content) return NULL;

        // Il vetro del menu sta in una finestra figlia (come barra e coda, CinecoreSurface) che si rimette in cima a
        // ogni apertura: dentro la finestra di Avalonia restava sotto la barra della musica, che doveva sparire.
        NSRect frame = [window contentRectForFrameRect:window.frame];
        NSWindow *child = [[NSWindow alloc] initWithContentRect:frame styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
        child.opaque = NO;
        child.backgroundColor = NSColor.clearColor;
        child.hasShadow = NO;
        child.ignoresMouseEvents = YES;
        child.releasedWhenClosed = NO;
        child.animationBehavior = NSWindowAnimationBehaviorNone;
        child.collectionBehavior = NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorMoveToActiveSpace;
        CinecoreGlassOverlay *overlay = [[CinecoreGlassOverlay alloc] initWithFrame:NSMakeRect(0, 0, frame.size.width, frame.size.height)];
        overlay.appearance = [NSAppearance appearanceNamed:NSAppearanceNameDarkAqua];
        overlay.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        child.contentView = overlay;
        [window addChildWindow:child ordered:NSWindowAbove];
        objc_setAssociatedObject(overlay, &CinecoreGlassParentKey, window, OBJC_ASSOCIATION_ASSIGN);
        objc_setAssociatedObject(overlay, &CinecoreGlassChildKey, child, OBJC_ASSOCIATION_RETAIN);
        return (__bridge_retained void *)overlay;
    }
#endif
    return NULL;
}

void CinecoreGlassSetPanels(void *handle, const NSRect *frames, NSUInteger count, double radius)
{
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
    if (@available(macOS 26.0, *)) {
        NSView *overlay = (__bridge NSView *)handle;
        NSWindow *child = objc_getAssociatedObject(overlay, &CinecoreGlassChildKey);
        NSWindow *parent = objc_getAssociatedObject(overlay, &CinecoreGlassParentKey);
        if (child && parent) {
            NSRect target = [parent contentRectForFrameRect:parent.frame];
            if (!NSEqualRects(child.frame, target)) [child setFrame:target display:NO];
            // In cima alle altre figlie (barra e coda): il menu si apre sopra di loro.
            if (count > 0 && parent.childWindows.lastObject != child) { [parent removeChildWindow:child]; [parent addChildWindow:child ordered:NSWindowAbove]; }
            if (!child.isVisible) [child orderFront:nil];
        }
        while (overlay.subviews.count > count) [overlay.subviews.lastObject removeFromSuperview];
        for (NSUInteger i = 0; i < count; ++i) {
            NSGlassEffectView *panel;
            if (i < overlay.subviews.count) {
                panel = (NSGlassEffectView *)overlay.subviews[i];
            } else {
                panel = [[NSGlassEffectView alloc] initWithFrame:frames[i]];
                panel.cornerRadius = radius;
                panel.tintColor = [NSColor colorWithWhite:0.06 alpha:0.30]; // menu: vetro vero, il contrasto lo danno luce sui bordi e voci chiare
                NSImageView *image = [[NSImageView alloc] initWithFrame:panel.bounds];
                image.imageScaling = NSImageScaleAxesIndependently;
                image.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
                panel.contentView = image;
                [overlay addSubview:panel];
            }
            panel.frame = frames[i];
        }
    }
#endif
}

// Tema dell'app: vetro chiaro (appena velato di bianco) o scuro per i pannelli del menu.
void CinecoreGlassSetTheme(void *handle, int dark)
{
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
    if (@available(macOS 26.0, *)) {
        NSView *overlay = (__bridge NSView *)handle;
        overlay.appearance = [NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua];
        for (NSView *view in overlay.subviews)
            if ([view isKindOfClass:[NSGlassEffectView class]])
                ((NSGlassEffectView *)view).tintColor = dark ? [NSColor colorWithWhite:0.06 alpha:0.30] : [NSColor colorWithWhite:1 alpha:0.12];
    }
#endif
}

void CinecoreGlassSetForeground(void *handle, int index, const void *pixels,
                                int width, int height, int stride)
{
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
    if (@available(macOS 26.0, *)) {
        NSView *overlay = (__bridge NSView *)handle;
        if (index < 0 || (NSUInteger)index >= overlay.subviews.count) return;
        NSGlassEffectView *panel = (NSGlassEffectView *)overlay.subviews[index];
        NSData *data = [NSData dataWithBytes:pixels length:(NSUInteger)stride * height];
        CGDataProviderRef provider = CGDataProviderCreateWithCFData((__bridge CFDataRef)data);
        CGColorSpaceRef space = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
        CGImageRef image = CGImageCreate(width, height, 8, 32, stride, space,
            kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst,
            provider, NULL, false, kCGRenderingIntentDefault);
        if (image) {
            ((NSImageView *)panel.contentView).image = [[NSImage alloc] initWithCGImage:image size:panel.bounds.size];
            CGImageRelease(image);
        }
        CGColorSpaceRelease(space);
        CGDataProviderRelease(provider);
    }
#endif
}

void CinecoreGlassClose(void *handle)
{
    NSView *overlay = (__bridge_transfer NSView *)handle;
    NSWindow *child = objc_getAssociatedObject(overlay, &CinecoreGlassChildKey);
    NSWindow *parent = objc_getAssociatedObject(overlay, &CinecoreGlassParentKey);
    if (child) { [parent removeChildWindow:child]; [child orderOut:nil]; child.contentView = nil; }
    else [overlay removeFromSuperview];
}

// ---------------------------------------------------------------------------------------------------------------
// Superfici di vetro sopra la pagina (barra della musica, coda): una finestra figlia trasparente sopra quella di
// Avalonia, che ignora il mouse (i clic arrivano ad Avalonia). Dentro, un pannello per superficie:
// macOS 26 -> NSGlassEffectView (Liquid Glass); prima -> NSVisualEffectView che sfoca dal vivo cio' che sta sotto
// la finestra figlia, cioe' la pagina. Il contenuto (testi, icone) arriva come immagine e sta sopra il vetro.
// Le viste NON vanno dentro la finestra di Avalonia: smetterebbe di disegnare.

@interface CinecoreSurfaceContent : NSView
@end
@implementation CinecoreSurfaceContent
- (BOOL)isFlipped { return YES; }
- (NSView *)hitTest:(NSPoint)point { return nil; }
@end

static NSRect CinecoreParentContentRect(NSWindow *parent)
{
    // frame e' gia' in coordinate dello schermo.
    return [parent contentRectForFrameRect:parent.frame];
}

static char CinecoreSurfaceParentKey;

void *CinecoreSurfaceCreate(void *parentHandle)
{
    NSWindow *parent = (__bridge NSWindow *)parentHandle;
    if (!parent) return NULL;
    NSWindow *child = [[NSWindow alloc] initWithContentRect:CinecoreParentContentRect(parent)
                                                  styleMask:NSWindowStyleMaskBorderless
                                                    backing:NSBackingStoreBuffered
                                                      defer:NO];
    child.opaque = NO;
    child.backgroundColor = NSColor.clearColor;
    child.hasShadow = NO;
    child.ignoresMouseEvents = YES;
    child.releasedWhenClosed = NO;
    child.animationBehavior = NSWindowAnimationBehaviorNone;
    child.collectionBehavior = NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorMoveToActiveSpace;
    CinecoreSurfaceContent *content = [[CinecoreSurfaceContent alloc] initWithFrame:NSMakeRect(0, 0, child.frame.size.width, child.frame.size.height)];
    content.wantsLayer = YES;
    child.contentView = content;
    [parent addChildWindow:child ordered:NSWindowAbove];
    // La madre si ricorda a parte: una finestra figlia nascosta con orderOut viene staccata da macOS e parentWindow torna
    // nil (la barra della musica spariva per sempre dopo il primo menu che la copriva).
    objc_setAssociatedObject(child, &CinecoreSurfaceParentKey, parent, OBJC_ASSOCIATION_ASSIGN);
    return (__bridge_retained void *)child;
}

static NSImageView *CinecoreSurfaceImage(NSView *panel)
{
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
    if (@available(macOS 26.0, *)) {
        if ([panel isKindOfClass:[NSGlassEffectView class]]) return (NSImageView *)((NSGlassEffectView *)panel).contentView;
    }
#endif
    return (NSImageView *)panel.subviews.firstObject;
}

/// frames in punti, coordinate della finestra di Avalonia (origine in alto a sinistra). dark: 1 tema scuro.
void CinecoreSurfaceSetPanels(void *handle, const NSRect *frames, const double *radii, NSUInteger count, int dark, double alpha)
{
    NSWindow *child = (__bridge NSWindow *)handle;
    NSWindow *parent = child.parentWindow ?: (NSWindow *)objc_getAssociatedObject(child, &CinecoreSurfaceParentKey);
    if (!parent) return;
    if (child.parentWindow == nil) [parent addChildWindow:child ordered:NSWindowAbove];
    [NSAnimationContext beginGrouping];
    [NSAnimationContext.currentContext setDuration:0];
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    NSRect target = CinecoreParentContentRect(parent);
    if (!NSEqualRects(child.frame, target)) [child setFrame:target display:NO];
    NSView *content = child.contentView;
    content.frame = NSMakeRect(0, 0, target.size.width, target.size.height);
    child.alphaValue = alpha;
    NSAppearance *appearance = [NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua];
    content.appearance = appearance;
    while (content.subviews.count > count) [content.subviews.lastObject removeFromSuperview];
    for (NSUInteger i = 0; i < count; ++i) {
        NSView *panel = i < content.subviews.count ? content.subviews[i] : nil;
        if (!panel) {
            BOOL liquid = NO;
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
            if (@available(macOS 26.0, *)) {
                NSGlassEffectView *glass = [[NSGlassEffectView alloc] initWithFrame:frames[i]];
                NSImageView *image = [[NSImageView alloc] initWithFrame:glass.bounds];
                image.imageScaling = NSImageScaleAxesIndependently;
                image.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
                glass.contentView = image;
                panel = glass; liquid = YES;
            }
#endif
            if (!liquid) {
                NSVisualEffectView *fx = [[NSVisualEffectView alloc] initWithFrame:frames[i]];
                fx.material = NSVisualEffectMaterialMenu;
                fx.blendingMode = NSVisualEffectBlendingModeBehindWindow;
                fx.state = NSVisualEffectStateActive;
                fx.wantsLayer = YES;
                fx.layer.masksToBounds = YES;
                NSImageView *image = [[NSImageView alloc] initWithFrame:fx.bounds];
                image.imageScaling = NSImageScaleAxesIndependently;
                image.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
                [fx addSubview:image];
                panel = fx;
            }
            [content addSubview:panel];
        }
        panel.frame = frames[i];
#if __MAC_OS_X_VERSION_MAX_ALLOWED >= 260000
        if (@available(macOS 26.0, *)) {
            if ([panel isKindOfClass:[NSGlassEffectView class]]) {
                NSGlassEffectView *glass = (NSGlassEffectView *)panel;
                glass.cornerRadius = radii[i];
                glass.tintColor = dark ? [NSColor colorWithWhite:0.09 alpha:0.32] : [NSColor colorWithWhite:1 alpha:0.10];
                continue;
            }
        }
#endif
        panel.layer.cornerRadius = radii[i];
    }
    [CATransaction commit];
    [NSAnimationContext endGrouping];
    // Nascosta con la trasparenza (alpha 0, nessun pannello), mai con orderOut: resta figlia della finestra dell'app.
    if (alpha > 0 && !child.isVisible) [child orderFront:nil];
}

void CinecoreSurfaceSetForeground(void *handle, int index, const void *pixels, int width, int height, int stride)
{
    NSWindow *child = (__bridge NSWindow *)handle;
    NSView *content = child.contentView;
    if (index < 0 || (NSUInteger)index >= content.subviews.count) return;
    NSView *panel = content.subviews[index];
    NSImageView *view = CinecoreSurfaceImage(panel);
    if (!view) return;
    NSData *data = [NSData dataWithBytes:pixels length:(NSUInteger)stride * height];
    CGDataProviderRef provider = CGDataProviderCreateWithCFData((__bridge CFDataRef)data);
    CGColorSpaceRef space = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    CGImageRef image = CGImageCreate(width, height, 8, 32, stride, space,
        kCGBitmapByteOrder32Little | kCGImageAlphaPremultipliedFirst, provider, NULL, false, kCGRenderingIntentDefault);
    if (image) {
        [CATransaction begin];
        [CATransaction setDisableActions:YES];
        view.image = [[NSImage alloc] initWithCGImage:image size:panel.bounds.size];
        [CATransaction commit];
        CGImageRelease(image);
    }
    CGColorSpaceRelease(space);
    CGDataProviderRelease(provider);
}

void CinecoreSurfaceClose(void *handle)
{
    NSWindow *child = (__bridge_transfer NSWindow *)handle;
    [child.parentWindow removeChildWindow:child];
    [child orderOut:nil];
    [child close];
}

// Finestra video di mpv adottata dal player: la comanda il player (stessa cornice, finestra figlia). mpv la vincola
// allo schermo con cui e' partito (constrainFrameRect:toScreen:) e, quando quello schermo si spegne o gli schermi si
// riordinano, la riportava sempre li': video su un altro schermo e player nero. Una sottoclasse al volo (come fa il
// KVO) restituisce la cornice cosi' com'e'.
static NSRect CinecoreKeepFrame(id self, SEL _cmd, NSRect frame, NSScreen *screen) { return frame; }

int CinecoreUnconstrainWindow(void *window)
{
    if (!window) return 0;
    NSWindow *w = (__bridge NSWindow *)window;
    Class base = object_getClass(w);
    if (strncmp(class_getName(base), "CinecoreFree_", 13) == 0) return 1;
    NSString *name = [NSString stringWithFormat:@"CinecoreFree_%s", class_getName(base)];
    Class sub = NSClassFromString(name);
    if (!sub)
    {
        sub = objc_allocateClassPair(base, name.UTF8String, 0);
        if (!sub) return 0;
        Method m = class_getInstanceMethod([NSWindow class], @selector(constrainFrameRect:toScreen:));
        class_addMethod(sub, @selector(constrainFrameRect:toScreen:), (IMP)CinecoreKeepFrame, method_getTypeEncoding(m));
        objc_registerClassPair(sub);
    }
    object_setClass(w, sub);
    return 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Finestra video di libmpv: mpv la crea e la mostra da sola, nel nostro processo, prima che l'app riesca ad
// agganciarla (timer da 100 ms). Per un attimo si vedeva spostata e piu' piccola ("un quadrato") e, a schermo
// intero, macOS cambiava Space perche' la finestra nuova non era ausiliaria. mpv le da' il titolo prima di mostrarla:
// li' la si rende invisibile, ausiliaria e libera dai vincoli dello schermo. L'app la mette al posto giusto e poi la
// rende visibile (MpvEngine.SyncFrame).
static IMP gCinecoreOrigSetTitle, gCinecoreOrigSetCollection;
static char gCinecoreMpvKey;
static void CinecoreSetCollectionHook(id self, SEL _cmd, NSWindowCollectionBehavior behavior)
{
    // mpv rimette "finestra principale dello schermo intero" dopo il titolo: la sua resta sempre ausiliaria.
    if (objc_getAssociatedObject(self, &gCinecoreMpvKey))
        behavior = (behavior & ~(NSWindowCollectionBehaviorFullScreenPrimary | NSWindowCollectionBehaviorManaged)) | NSWindowCollectionBehaviorFullScreenAuxiliary;
    ((void (*)(id, SEL, NSWindowCollectionBehavior))gCinecoreOrigSetCollection)(self, _cmd, behavior);
}

static void CinecoreSetTitleHook(id self, SEL _cmd, NSString *title)
{
    ((void (*)(id, SEL, NSString *))gCinecoreOrigSetTitle)(self, _cmd, title);
    if (![title isKindOfClass:[NSString class]] || !([title hasPrefix:@"Cinecore MPV Video"] || [title isEqualToString:@"mpv"])) return; // "mpv" = nome alla creazione
    NSWindow *w = (NSWindow *)self;
    if (objc_getAssociatedObject(w, &gCinecoreMpvKey)) return; // solo alla nascita: mpv puo' rimettere il titolo dopo
    objc_setAssociatedObject(w, &gCinecoreMpvKey, @YES, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    w.alphaValue = 0;
    w.hasShadow = NO;
    w.collectionBehavior = w.collectionBehavior; // passa dal gancio qui sopra: resta ausiliaria
    CinecoreUnconstrainWindow((__bridge void *)w);
}

int CinecoreHideNewMpvWindows(void)
{
    if (gCinecoreOrigSetTitle) return 1;
    Method m = class_getInstanceMethod([NSWindow class], @selector(setTitle:));
    if (!m) return 0;
    gCinecoreOrigSetTitle = method_setImplementation(m, (IMP)CinecoreSetTitleHook);
    Method c = class_getInstanceMethod([NSWindow class], @selector(setCollectionBehavior:));
    if (c) gCinecoreOrigSetCollection = method_setImplementation(c, (IMP)CinecoreSetCollectionHook);
    return 1;
}

// Finestra video di mpv: finche' mpv non ridisegna alla misura nuova (2-3 fotogrammi dopo un cambio di misura, es.
// uscita dallo schermo intero) il livello Metal tiene il fotogramma vecchio ancorato in alto a sinistra e tagliato:
// righe e salti ai bordi. Con "resize" il fotogramma vecchio si adatta alla misura nuova finche' arriva quello giusto.
static void CinecoreGravityResize(CALayer *layer)
{
    if (!layer) return;
    if (![layer.contentsGravity isEqualToString:kCAGravityResize]) layer.contentsGravity = kCAGravityResize;
    for (CALayer *sub in layer.sublayers) CinecoreGravityResize(sub);
}

int CinecoreVideoLayersResize(void *window)
{
    if (!window) return 0;
    NSView *content = ((__bridge NSWindow *)window).contentView;
    if (!content) return 0;
    NSMutableArray<NSView *> *views = [NSMutableArray arrayWithObject:content];
    for (NSUInteger i = 0; i < views.count; i++)
    {
        NSView *v = views[i];
        CinecoreGravityResize(v.layer);
        [views addObjectsFromArray:v.subviews];
    }
    return 1;
}

// Icona dell'app bloccata su quella di Cinecore: libmpv (video, audio della musica, audio affiancato) imposta la propria
// icona a ogni creazione e nel Dock si alternavano le due. Dopo il blocco ogni setApplicationIconImage: mette la nostra.
static IMP gCinecoreOrigSetIcon;
static NSImage *gCinecoreLockedIcon;
static void CinecoreSetIconHook(id self, SEL _cmd, NSImage *image)
{
    ((void (*)(id, SEL, NSImage *))gCinecoreOrigSetIcon)(self, _cmd, gCinecoreLockedIcon ?: image);
}

int CinecoreLockAppIcon(void *image)
{
    if (!image) return 0;
    gCinecoreLockedIcon = (__bridge NSImage *)image;
    if (!gCinecoreOrigSetIcon)
    {
        Method m = class_getInstanceMethod([NSApplication class], @selector(setApplicationIconImage:));
        if (!m) return 0;
        gCinecoreOrigSetIcon = method_setImplementation(m, (IMP)CinecoreSetIconHook);
    }
    [NSApp setApplicationIconImage:gCinecoreLockedIcon];
    return 1;
}
