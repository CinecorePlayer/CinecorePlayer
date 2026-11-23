import AppKit
// Icona Cinecore: logo su un quadrato arrotondato di vetro (stile Liquid Glass di macOS 26): vetro chiaro
// semitrasparente, luce sul bordo (piu' forte in alto a sinistra), riflesso morbido in alto. Griglia Apple 824/1024.
let logo = NSImage(contentsOfFile: CommandLine.arguments[1])!
let outDir = CommandLine.arguments[2]
func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let k = CGFloat(px) / 1024
    let body = NSRect(x: 100 * k, y: 100 * k, width: 824 * k, height: 824 * k)
    let r = 185 * k
    let path = NSBezierPath(roundedRect: body, xRadius: r, yRadius: r)
    // Ombra morbida sotto il vetro.
    let shadow = NSShadow(); shadow.shadowColor = NSColor(white: 0, alpha: 0.28); shadow.shadowBlurRadius = 22 * k; shadow.shadowOffset = NSSize(width: 0, height: -10 * k)
    NSGraphicsContext.saveGraphicsState(); shadow.set(); NSColor(white: 1, alpha: 0.22).setFill(); path.fill(); NSGraphicsContext.restoreGraphicsState()
    // Corpo di vetro: chiaro e trasparente, un po' piu' denso in basso.
    NSGraphicsContext.saveGraphicsState(); path.addClip()
    NSGradient(colors: [NSColor(white: 1, alpha: 0.30), NSColor(white: 0.92, alpha: 0.16), NSColor(white: 0.85, alpha: 0.24)])!.draw(in: body, angle: -90)
    // Riflesso in alto (luce che entra) e alone in basso a destra (luce riflessa).
    NSGradient(colors: [NSColor(white: 1, alpha: 0.42), NSColor(white: 1, alpha: 0)])!.draw(in: NSRect(x: body.minX, y: body.maxY - body.height * 0.42, width: body.width, height: body.height * 0.42), angle: -90)
    let glow = NSBezierPath(ovalIn: NSRect(x: body.midX, y: body.minY - body.height * 0.25, width: body.width * 0.8, height: body.height * 0.55))
    NSGradient(colors: [NSColor(white: 1, alpha: 0.16), NSColor(white: 1, alpha: 0)])!.draw(in: glow, relativeCenterPosition: .zero)
    NSGraphicsContext.restoreGraphicsState()
    // Bordo luminoso: forte in alto a sinistra, tenue sui lati, di nuovo acceso in basso a destra.
    let rim = NSBezierPath(roundedRect: body.insetBy(dx: 1.5 * k, dy: 1.5 * k), xRadius: r - 1.5 * k, yRadius: r - 1.5 * k)
    rim.lineWidth = max(1, 3 * k)
    NSGraphicsContext.saveGraphicsState()
    let rimImage = NSImage(size: NSSize(width: px, height: px), flipped: false) { _ in
        NSColor.white.setStroke(); rim.stroke(); return true
    }
    let mask = NSGradient(colorsAndLocations: (NSColor(white: 1, alpha: 0.95), 0.0), (NSColor(white: 1, alpha: 0.18), 0.45), (NSColor(white: 1, alpha: 0.18), 0.6), (NSColor(white: 1, alpha: 0.6), 1.0))!
    // Il bordo si disegna con l'opacita' del gradiente diagonale.
    let tmp = NSImage(size: NSSize(width: px, height: px), flipped: false) { rect in
        rimImage.draw(in: rect)
        NSGraphicsContext.current?.compositingOperation = .sourceIn
        mask.draw(in: rect, angle: -45)
        return true
    }
    tmp.draw(in: NSRect(x: 0, y: 0, width: px, height: px))
    NSGraphicsContext.restoreGraphicsState()
    // Logo al centro, con un'ombra leggera per staccarsi dal vetro.
    let side = 640 * k
    let ls = NSShadow(); ls.shadowColor = NSColor(white: 0, alpha: 0.30); ls.shadowBlurRadius = 14 * k; ls.shadowOffset = NSSize(width: 0, height: -6 * k)
    NSGraphicsContext.saveGraphicsState(); ls.set()
    logo.draw(in: NSRect(x: (CGFloat(px) - side) / 2, y: (CGFloat(px) - side) / 2, width: side, height: side), from: .zero, operation: .sourceOver, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}
for (name, px) in [("16x16",16),("16x16@2x",32),("32x32",32),("32x32@2x",64),("128x128",128),("128x128@2x",256),("256x256",256),("256x256@2x",512),("512x512",512),("512x512@2x",1024)] {
    try! render(px).write(to: URL(fileURLWithPath: "\(outDir)/icon_\(name).png"))
}
