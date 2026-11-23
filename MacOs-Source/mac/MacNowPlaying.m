// Copertina per "In riproduzione" di macOS (MPNowPlayingInfoCenter). Su macOS MPMediaItemArtwork si crea solo con un
// blocco (initWithBoundsSize:requestHandler:), che da C# non si puo' scrivere: lo fa questa funzione.
#import <AppKit/AppKit.h>
#import <MediaPlayer/MediaPlayer.h>

// Restituisce un MPMediaItemArtwork trattenuto (da liberare con CinecoreNowPlayingRelease), NULL se l'immagine non si legge.
void *CinecoreNowPlayingArtwork(const char *path)
{
    if (path == NULL) return NULL;
    @autoreleasepool {
        NSImage *image = [[NSImage alloc] initWithContentsOfFile:[NSString stringWithUTF8String:path]];
        if (image == nil || image.size.width < 1 || image.size.height < 1) return NULL;
        MPMediaItemArtwork *artwork = [[MPMediaItemArtwork alloc] initWithBoundsSize:image.size
            requestHandler:^NSImage * _Nonnull(CGSize size) { (void)size; return image; }];
        return (__bridge_retained void *)artwork;
    }
}

void CinecoreNowPlayingRelease(void *artwork)
{
    if (artwork != NULL) CFRelease(artwork);
}
