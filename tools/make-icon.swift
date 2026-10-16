// 生成应用图标的各个尺寸：swift tools/make-icon.swift <输出的 .iconset 目录>
// 绿色渐变圆角方块 + 白色链接图形，按 macOS 图标网格（1024 画布里 824 的方块）画
import AppKit

let dir = CommandLine.arguments[1]
try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)

func srgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: r, green: g, blue: b, alpha: a)
}

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .calibratedRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: px, height: px)
    let gc = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = gc
    let ctx = gc.cgContext
    ctx.scaleBy(x: CGFloat(px) / 1024, y: CGFloat(px) / 1024)
    let space = CGColorSpace(name: CGColorSpace.sRGB)!

    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: tile, cornerWidth: 186, cornerHeight: 186, transform: nil)

    // 底下的投影
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: srgb(0, 0.16, 0.08, 0.32))
    ctx.addPath(shape)
    ctx.setFillColor(srgb(0.09, 0.64, 0.38))
    ctx.fillPath()
    ctx.restoreGState()

    // 渐变底色 + 顶上一层淡高光
    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()
    let base = CGGradient(colorsSpace: space, colors: [srgb(0.33, 0.86, 0.60), srgb(0.06, 0.56, 0.33)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(base, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])
    let gloss = CGGradient(colorsSpace: space, colors: [srgb(1, 1, 1, 0.2), srgb(1, 1, 1, 0)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(gloss, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 540), options: [])
    ctx.restoreGState()

    // 白色链接图形
    let cfg = NSImage.SymbolConfiguration(pointSize: 400, weight: .semibold)
        .applying(NSImage.SymbolConfiguration(paletteColors: [.white]))
    let sym = NSImage(systemSymbolName: "link", accessibilityDescription: nil)!.withSymbolConfiguration(cfg)!
    let s = sym.size
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 22, color: srgb(0, 0.25, 0.12, 0.35))
    sym.draw(in: CGRect(x: 512 - s.width / 2, y: 512 - s.height / 2, width: s.width, height: s.height))
    ctx.restoreGState()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let sizes: [(String, Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
for (name, px) in sizes {
    try! render(px).write(to: URL(fileURLWithPath: "\(dir)/\(name).png"))
}
