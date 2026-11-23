// Sessione web per Genius (GeniusBrowserSession di Windows, che usa WebView2): una WKWebView nascosta con i cookie
// persistenti di WebKit legge le pagine di genius.com quando la richiesta diretta riceve 403 (verifica del sito).
// Se il sito vuole una verifica a mano, "Genius: verifica connessione" mostra la stessa vista in una finestra:
// nessun cookie si inventa e nessuna verifica si automatizza.
#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>

typedef void (*CinecoreWebTextCallback)(const char *text, void *context);
typedef void (*CinecoreWebClosedCallback)(void);

@interface CinecoreGeniusSession : NSObject <WKNavigationDelegate, NSWindowDelegate>
@property(nonatomic, strong) WKWebView *view;
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, copy) NSString *pending;
@property(nonatomic, copy) NSString *lastUrl;
@property(nonatomic) CinecoreWebTextCallback callback;
@property(nonatomic) void *context;
@property(nonatomic) NSInteger generation;
@property(nonatomic) CinecoreWebClosedCallback closed;
@end

static CinecoreGeniusSession *Session;

static BOOL GeniusHost(NSURL *url)
{
    NSString *host = url.host.lowercaseString;
    return [url.scheme isEqualToString:@"https"] && ([host isEqualToString:@"genius.com"] || [host hasSuffix:@".genius.com"]);
}

@implementation CinecoreGeniusSession

- (instancetype)init
{
    if ((self = [super init])) {
        WKWebViewConfiguration *configuration = [WKWebViewConfiguration new];
        configuration.websiteDataStore = WKWebsiteDataStore.defaultDataStore; // cookie persistenti, come il profilo di WebView2
        _view = [[WKWebView alloc] initWithFrame:NSMakeRect(0, 0, 960, 720) configuration:configuration];
        _view.navigationDelegate = self;
        _window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 960, 720)
                                              styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable
                                                backing:NSBackingStoreBuffered defer:YES];
        _window.title = @"Genius — verifica connessione";
        _window.releasedWhenClosed = NO;
        _window.delegate = self;
        _window.contentView = _view;
    }
    return self;
}

// Solo pagine https di genius.com (NavigationStarting di Windows).
- (void)webView:(WKWebView *)webView decidePolicyForNavigationAction:(WKNavigationAction *)action decisionHandler:(void (^)(WKNavigationActionPolicy))decision
{
    NSURL *url = action.request.URL;
    BOOL allowed = GeniusHost(url) || [url.scheme isEqualToString:@"about"];
    if (!action.targetFrame) allowed = NO; // niente finestre nuove
    decision(allowed ? WKNavigationActionPolicyAllow : WKNavigationActionPolicyCancel);
}

- (void)finish:(NSString *)text generation:(NSInteger)generation
{
    if (generation != self.generation || !self.callback) return;
    CinecoreWebTextCallback callback = self.callback;
    void *context = self.context;
    self.callback = NULL; self.context = NULL; self.pending = nil;
    callback(text ? text.UTF8String : NULL, context);
    if (!self.window.visible) [self.view stopLoading];
}

// Pagina pronta quando contiene cio' che serve (come "usable" su Windows): JSON con "response" per l'API,
// i contenitori del testo o og:image per le pagine. Si controlla ogni mezzo secondo fino allo scadere.
- (void)poll:(NSInteger)generation deadline:(NSDate *)deadline
{
    if (generation != self.generation || !self.callback) return;
    BOOL api = [self.pending containsString:@"/api/"];
    NSString *script = api ? @"document.body ? document.body.innerText : ''" : @"document.documentElement ? document.documentElement.outerHTML : ''";
    [self.view evaluateJavaScript:script completionHandler:^(id result, NSError *error) {
        NSString *text = [result isKindOfClass:NSString.class] ? result : nil;
        BOOL usable = text && (api ? ([[text stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet] hasPrefix:@"{"] && [text containsString:@"\"response\""])
                                   : ([text rangeOfString:@"data-lyrics-container" options:NSCaseInsensitiveSearch].location != NSNotFound ||
                                      [text rangeOfString:@"og:image" options:NSCaseInsensitiveSearch].location != NSNotFound));
        if (usable && [self.view.URL.absoluteString isEqualToString:self.pending]) { [self finish:text generation:generation]; return; }
        if ([deadline timeIntervalSinceNow] <= 0) { [self finish:nil generation:generation]; return; }
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 500 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{ [self poll:generation deadline:deadline]; });
    }];
}

- (void)fetch:(NSString *)url timeout:(double)seconds callback:(CinecoreWebTextCallback)callback context:(void *)context
{
    // La finestra di verifica aperta: la vista e' dell'utente, la lettura aspetta (come _form.Visible su Windows).
    if (self.window.visible || !GeniusHost([NSURL URLWithString:url])) { callback(NULL, context); return; }
    if (self.callback) { CinecoreWebTextCallback old = self.callback; void *oldContext = self.context; self.callback = NULL; old(NULL, oldContext); }
    NSInteger generation = ++self.generation;
    self.callback = callback; self.context = context;
    self.pending = url; self.lastUrl = url;
    [self.view loadRequest:[NSURLRequest requestWithURL:[NSURL URLWithString:url]]];
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:seconds];
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 700 * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{ [self poll:generation deadline:deadline]; });
}

- (void)show
{
    if (self.callback) { CinecoreWebTextCallback old = self.callback; void *oldContext = self.context; self.callback = NULL; self.generation++; old(NULL, oldContext); }
    NSString *url = self.lastUrl ?: @"https://genius.com/";
    [self.window center];
    [self.window makeKeyAndOrderFront:nil];
    [self.view loadRequest:[NSURLRequest requestWithURL:[NSURL URLWithString:url]]];
}

- (BOOL)windowShouldClose:(NSWindow *)sender
{
    [sender orderOut:nil];
    if (self.closed) self.closed();
    return NO;
}
@end

static CinecoreGeniusSession *EnsureSession(void)
{
    if (!Session) Session = [CinecoreGeniusSession new];
    return Session;
}

void CinecoreWebFetch(const char *url, double timeoutSeconds, CinecoreWebTextCallback callback, void *context)
{
    NSString *address = [NSString stringWithUTF8String:url];
    dispatch_async(dispatch_get_main_queue(), ^{ [EnsureSession() fetch:address timeout:timeoutSeconds callback:callback context:context]; });
}

void CinecoreWebShowVerification(CinecoreWebClosedCallback closed)
{
    dispatch_async(dispatch_get_main_queue(), ^{ CinecoreGeniusSession *session = EnsureSession(); session.closed = closed; [session show]; });
}
