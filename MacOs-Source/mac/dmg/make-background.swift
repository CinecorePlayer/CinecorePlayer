// Sfondo della finestra del .dmg di Cinecore Player (660x420 punti): fondo scuro con alone viola come la grafica
// dell'installer di Windows, logo in alto, freccia dall'app ad Applicazioni e due righe di istruzioni.
// Uso: swift make-background.swift <logo.png> <uscita.png> <scala 1|2>
import AppKit
import CoreGraphics

let args = CommandLine.arguments
let logo = NSImage(contentsOfFile: args[1])!
let out = args[2]
let scale = CGFloat(Double(args[3]) ?? 1)
let W: CGFloat = 660, H: CGFloat = 420

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(W * scale), pixelsHigh: Int(H * scale), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
rep.size = NSSize(width: W, height: H)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

// Fondo: blu notte verso nero, con alone viola dietro al logo e uno piu' tenue in basso.
let space = CGColorSpaceCreateDeviceRGB()
let base = CGGradient(colorsSpace: space, colors: [NSColor(red: 0.07, green: 0.06, blue: 0.13, alpha: 1).cgColor,
                                                   NSColor(red: 0.02, green: 0.02, blue: 0.04, alpha: 1).cgColor] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(base, start: CGPoint(x: 0, y: H), end: CGPoint(x: 0, y: 0), options: [])
func glow(_ c: CGPoint, _ r: CGFloat, _ color: NSColor) {
    let g = CGGradient(colorsSpace: space, colors: [color.cgColor, color.withAlphaComponent(0).cgColor] as CFArray, locations: [0, 1])!
    ctx.drawRadialGradient(g, startCenter: c, startRadius: 0, endCenter: c, endRadius: r, options: [])
}
glow(CGPoint(x: W / 2, y: H - 40), 330, NSColor(red: 0.42, green: 0.16, blue: 0.62, alpha: 0.55))
glow(CGPoint(x: W * 0.2, y: 40), 260, NSColor(red: 0.85, green: 0.20, blue: 0.45, alpha: 0.16))
glow(CGPoint(x: W * 0.85, y: 70), 240, NSColor(red: 0.25, green: 0.30, blue: 0.85, alpha: 0.14))

// Logo (simbolo + scritta bianca) in alto al centro.
let logoH: CGFloat = 54, logoW = logoH * logo.size.width / logo.size.height
logo.draw(in: NSRect(x: (W - logoW) / 2, y: H - 30 - logoH, width: logoW, height: logoH), from: .zero, operation: .sourceOver, fraction: 1)

// Freccia fra le due icone (centri a x 175 e 485, y 210 dall'alto -> 210 dal basso).
let y: CGFloat = H - 205
let arrow = NSBezierPath()
arrow.move(to: NSPoint(x: 265, y: y)); arrow.line(to: NSPoint(x: 385, y: y))
arrow.lineWidth = 3; arrow.lineCapStyle = .round
NSColor(white: 1, alpha: 0.55).setStroke(); arrow.stroke()
let head = NSBezierPath()
head.move(to: NSPoint(x: 372, y: y + 11)); head.line(to: NSPoint(x: 388, y: y)); head.line(to: NSPoint(x: 372, y: y - 11))
head.lineWidth = 3; head.lineCapStyle = .round; head.lineJoinStyle = .round; head.stroke()

// Il Finder scrive i nomi delle icone sempre in nero: dietro ognuno una capsula chiara, se no sul fondo scuro non si legge.
for cx in [CGFloat(175), CGFloat(485)] {
    let pill = NSBezierPath(roundedRect: NSRect(x: cx - 84, y: H - 292, width: 168, height: 25), xRadius: 12, yRadius: 12)
    NSColor(white: 1, alpha: 0.86).setFill(); pill.fill()
}

// Testi.
func text(_ s: String, _ size: CGFloat, _ weight: NSFont.Weight, _ alpha: CGFloat, _ yTop: CGFloat) {
    let p = NSMutableParagraphStyle(); p.alignment = .center
    let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size, weight: weight),
                                            .foregroundColor: NSColor(white: 1, alpha: alpha), .paragraphStyle: p]
    NSAttributedString(string: s, attributes: a).draw(in: NSRect(x: 30, y: H - yTop - size * 1.5, width: W - 60, height: size * 1.6))
}
text("Trascina Cinecore Player nella cartella Applicazioni", 16, .semibold, 0.95, 318)
text("Al primo avvio scarica da solo Wine, madVR e MPC Video Renderer dai siti ufficiali.", 11.5, .regular, 0.62, 346)
text("Non è firmata da Apple: la prima volta aprila con clic destro › Apri.", 11.5, .regular, 0.62, 364)

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
