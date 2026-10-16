// 采集引擎：由「公众号链接采集.app」的界面在后台启动（也能在终端直接运行，方便调试）
// 先在微信里打开公众号主页（停在文章列表），再启动。参数：
//   --dir 目录       数据放哪儿（默认 ~/Documents/公众号链接采集）
//   --file 名字.md   存到哪个文件（默认按公众号名字，比如「数字生命卡兹克.md」）
//   --max N          本次最多新采 N 篇
//   --recheck        只做「从头再扫一遍补漏」
//   --probe          只看不点，打印认到的东西（调试用）
// 新文件会先翻回主页顶上从头采；采到底后自动从头再扫一遍，把漏掉的插回原位。
// 误碰鼠标会暂停、停手 5 秒继续；鼠标甩到主屏幕左上角或收到 SIGTERM 就彻底停。再次启动同一个文件会接着上次继续。
// 进度写在数据目录的 status.json，过程写在 运行日志.txt，界面靠这两个文件显示进度。
// 签名标识必须是英文：中文标识会让系统文字识别一直报错（build.sh 已处理）。
import AppKit
import ApplicationServices
import CoreGraphics
import Foundation
import Vision

// MARK: - 配置

var args = Array(CommandLine.arguments.dropFirst()).filter { !$0.hasPrefix("-psn") }
func takeValue(_ flag: String) -> String? {
    guard let i = args.firstIndex(of: flag), i + 1 < args.count else { return nil }
    defer { args.removeSubrange(i...(i + 1)) }
    return args[i + 1]
}
func takeFlag(_ flags: String...) -> Bool {
    let hit = args.contains { flags.contains($0) }
    args.removeAll { flags.contains($0) }
    return hit
}
let maxCount = takeValue("--max").flatMap { Int($0) } ?? Int.max
let runID = takeValue("--run-id") ?? ""
let dataDir = takeValue("--dir") ?? FileManager.default.homeDirectoryForCurrentUser.path + "/Documents/公众号链接采集"
var mdName = takeValue("--file")
let recheckOnly = takeFlag("--recheck", "--补漏")
let probeMode = takeFlag("--probe")
if mdName == nil { mdName = args.first { !$0.hasPrefix("--") } }
if let n = mdName, !n.hasSuffix(".md") { mdName = n + ".md" }

try? FileManager.default.createDirectory(atPath: dataDir, withIntermediateDirectories: true)
var mdPath = ""
var statePath = ""
let logPath = "\(dataDir)/运行日志.txt"
let statusPath = "\(dataDir)/status.json"
let pidPath = "\(dataDir)/.running.pid"
let tmpShot = NSTemporaryDirectory() + "wx_collect_shot.png"

let restEvery = 40       // 每采这么多篇歇一会儿

struct StopError: Error { let reason: String; var byUser = false }   // 彻底停
struct Interrupted: Error { let reason: String }   // 暂停，等你停手后自动继续
struct TextBox { let text: String; let rect: CGRect }   // 窗口内坐标（点）

// 本次运行的统计
var account = ""
var added = 0         // 新写进 md 的
var reopened = 0      // 点开了但链接已经有了（白开）
var recovered = 0     // 补漏时补到的
var unreadable = Set<String>()   // 看到阅读数却认不出标题的卡片
var durations: [Double] = []
var recovering = false
var mismatches: [String: Int] = [:]
var urls = Set<String>()
var stopRequested: Int32 = 0   // 收到停止指令（wxlinks.sh stop）

// MARK: - 进度和日志

var status: [String: Any] = ["state": "starting", "pid": Int(getpid()), "run_id": runID,
                             "started_at": ISO8601DateFormatter().string(from: Date())]

/// 进度写进 status.json，给 WorkBuddy / wxlinks.sh 看
func saveStatus(_ changes: [String: Any] = [:]) {
    if probeMode { return }
    for (k, v) in changes { status[k] = v }
    let avg = durations.isEmpty ? 0 : durations.reduce(0, +) / Double(durations.count)
    status.merge(["updated_at": ISO8601DateFormatter().string(from: Date()), "account": account, "md_path": mdPath,
                  "total_links": urls.count, "added_this_run": added, "recovered": recovered, "reopened": reopened,
                  "unreadable": unreadable.count, "avg_seconds": (avg * 10).rounded() / 10,
                  "phase": recovering ? "recheck" : "first_pass"]) { $1 }
    if let d = try? JSONSerialization.data(withJSONObject: status, options: [.prettyPrinted, .sortedKeys]) {
        try? d.write(to: URL(fileURLWithPath: statusPath), options: .atomic)
    }
}

func log(_ s: String) {
    let line = "[\(DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium))] \(s)"
    print(line)
    fflush(stdout)
    if let h = FileHandle(forWritingAtPath: logPath) {
        h.seekToEndOfFile(); h.write((line + "\n").data(using: .utf8)!); h.closeFile()
    } else {
        try? (line + "\n").write(toFile: logPath, atomically: true, encoding: .utf8)
    }
    saveStatus(["message": s])
}

func pause(_ lo: Double, _ hi: Double) { usleep(useconds_t(Double.random(in: lo...hi) * 1_000_000)) }

// MARK: - 微信窗口

let wechatBundles: Set<String> = ["com.tencent.xinWeChat", "com.tencent.flue.WeChatAppEx"]
let wechatApps = NSWorkspace.shared.runningApplications.filter { wechatBundles.contains($0.bundleIdentifier ?? "") }
let wechatPIDs: Set<pid_t> = Set(wechatApps.map { $0.processIdentifier })
let articleWindowTitle = "微信 (窗口)"   // 公众号主页单独弹出的窗口

func onScreenWindows() -> [[String: Any]] {
    (CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? [])
        .filter { ($0[kCGWindowLayer as String] as? Int) == 0 }
}

func rectOf(_ w: [String: Any]) -> CGRect {
    let b = w[kCGWindowBounds as String] as! [String: Any]
    return CGRect(x: b["X"] as! Double, y: b["Y"] as! Double, width: b["Width"] as! Double, height: b["Height"] as! Double)
}

/// 公众号主页所在的微信窗口：优先单独弹出的那个，没有就用最前面够大的微信窗口
func findWindow() -> CGRect? {
    let big = onScreenWindows().filter {
        guard let pid = $0[kCGWindowOwnerPID as String] as? pid_t, wechatPIDs.contains(pid) else { return false }
        let r = rectOf($0)
        return r.width > 600 && r.height > 500
    }
    let named = big.first { ($0[kCGWindowName as String] as? String) == articleWindowTitle }
    return (named ?? big.first).map(rectOf)
}

/// 把微信切到前面；开了「台前调度」时窗口会缩在边上，要单独把公众号主页窗口拉出来
func bringWeChatFront() {
    let p = Process(); p.launchPath = "/usr/bin/open"; p.arguments = ["-a", "WeChat"]; p.launch(); p.waitUntilExit()
    usleep(800_000)
    for pid in wechatPIDs {
        let app = AXUIElementCreateApplication(pid)
        var v: AnyObject?
        AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &v)
        for w in (v as? [AXUIElement]) ?? [] {
            var t: AnyObject?
            AXUIElementCopyAttributeValue(w, kAXTitleAttribute as CFString, &t)
            guard (t as? String) == articleWindowTitle else { continue }
            AXUIElementSetAttributeValue(w, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
            AXUIElementPerformAction(w, kAXRaiseAction as CFString)
            AXUIElementSetAttributeValue(w, kAXMainAttribute as CFString, kCFBooleanTrue)
            AXUIElementSetAttributeValue(w, kAXFocusedAttribute as CFString, kCFBooleanTrue)
            AXUIElementSetAttributeValue(app, kAXFocusedWindowAttribute as CFString, w)
        }
    }
    // 等窗口动画放完、尺寸稳定
    var last = CGRect.zero
    for _ in 0..<10 {
        usleep(300_000)
        let r = findWindow() ?? .zero
        if r != .zero && r == last { return }
        last = r
    }
}

func wechatIsFront() -> Bool {
    guard let w = onScreenWindows().first(where: { rectOf($0).width > 200 && rectOf($0).height > 200 }),
          let pid = w[kCGWindowOwnerPID as String] as? pid_t else { return false }
    return wechatPIDs.contains(pid)
}

var win = CGRect.zero

func refreshWindow() throws {
    guard let r = findWindow() else { throw StopError(reason: "找不到微信的公众号主页窗口（被切走、最小化或缩到边上了）") }
    win = r
}

// MARK: - 截图 + 认字

/// 截取窗口内一块区域（窗口内坐标）
func capture(_ r: CGRect) -> CGImage? {
    if let w = findWindow() { win = w }
    let g = r.offsetBy(dx: win.minX, dy: win.minY)
    let p = Process()
    p.launchPath = "/usr/sbin/screencapture"
    p.arguments = ["-x", "-R\(Int(g.minX)),\(Int(g.minY)),\(Int(g.width)),\(Int(g.height))", tmpShot]
    p.launch(); p.waitUntilExit()
    guard let img = NSImage(contentsOfFile: tmpShot) else { return nil }
    var rect = CGRect(origin: .zero, size: img.size)
    return img.cgImage(forProposedRect: &rect, context: nil, hints: nil)
}

/// 截取窗口内一块区域并认字；region 为空时截整个窗口
func snapshot(_ region: CGRect? = nil) -> [TextBox] {
    let r = region ?? CGRect(origin: .zero, size: win.size)
    guard let cg = capture(r) else { return [] }
    let req = VNRecognizeTextRequest()
    req.recognitionLevel = .accurate
    req.recognitionLanguages = ["zh-Hans", "en-US"]
    req.usesLanguageCorrection = false
    do { try VNImageRequestHandler(cgImage: cg).perform([req]) } catch { log("认字出错：\(error)") }
    return (req.results ?? []).compactMap { o in
        guard let t = o.topCandidates(1).first?.string else { return nil }
        let b = o.boundingBox
        return TextBox(text: t, rect: CGRect(x: r.minX + b.minX * r.width, y: r.minY + (1 - b.maxY) * r.height,
                                             width: b.width * r.width, height: b.height * r.height))
    }
}

/// 当前标签页的位置：标签栏里底色浅灰、宽 150~300 点的那一条（认字定位会差二十多点，看颜色更准）
func activeTab() -> CGRect? {
    let bar = CGRect(x: 0, y: 0, width: win.width, height: 70)
    guard let cg = capture(bar) else { return nil }
    let w = cg.width, h = cg.height
    var px = [UInt8](repeating: 0, count: w * h * 4)
    guard let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
    let s = Double(w) / Double(bar.width)
    var hits: [(y: Int, l: Int, r: Int)] = []
    for y in 0..<Int(bar.height) {
        let row = Int(Double(y) * s) * w * 4
        var runStart = -1
        for x in 0..<w {
            let i = row + x * 4
            let v = (Int(px[i]) + Int(px[i + 1]) + Int(px[i + 2])) / 3
            let light = v >= 226 && v <= 245   // 实测当前标签底色 237，其他标签和背景 250
            if light && runStart < 0 { runStart = x }
            if (!light || x == w - 1) && runStart >= 0 {
                let l = Int(Double(runStart) / s), r = Int(Double(x) / s)
                if r - l >= 150 && r - l <= 300 { hits.append((y, l, r)) }
                runStart = -1
            }
        }
    }
    // 同一个左右边界出现在多行里的，才算标签
    var count: [String: Int] = [:]
    for t in hits { count["\(t.l / 3),\(t.r / 3)", default: 0] += 1 }
    guard let best = count.max(by: { $0.value < $1.value }), best.value >= 4 else { return nil }
    let rows = hits.filter { "\($0.l / 3),\($0.r / 3)" == best.key }
    let l = rows.map { $0.l }.sorted()[rows.count / 2], r = rows.map { $0.r }.sorted()[rows.count / 2]
    let top = rows.map { $0.y }.min()!, bottom = rows.map { $0.y }.max()!
    return CGRect(x: l, y: top, width: r - l, height: bottom - top)
}

/// 图里每个像素的亮度（0~255），按行排
func brightness(_ cg: CGImage) -> [UInt8] {
    let w = cg.width, h = cg.height
    var px = [UInt8](repeating: 0, count: w * h * 4)
    if let ctx = CGContext(data: &px, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                           space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
        ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
    }
    var out = [UInt8](repeating: 0, count: w * h)
    for i in 0..<(w * h) {
        let sum = Int(px[i * 4]) + Int(px[i * 4 + 1]) + Int(px[i * 4 + 2])
        out[i] = UInt8(sum / 3)
    }
    return out
}

/// 在一张「标签右半边」的截图里找「···」：标签灰底范围内最右边那团深色图形（外圈加三个点，宽十几点）。
/// 它左边的「⊗ / ↻」（停止加载 / 刷新）颜色浅，不会被认成它。ptWidth 是这张图对应多少点宽，返回中心离图左边多少点
func dotsCenterX(_ cg: CGImage, ptWidth: Double) -> Double? {
    let w = cg.width, h = cg.height
    let v = brightness(cg)
    let s = Double(w) / ptWidth
    let bgRow = min(h - 1, Int(3 * s))   // 标签顶上 3 点：在图标上面，只有底色
    let rows = Int(Double(h) * 0.25)..<Int(Double(h) * 0.85)
    var cols = [Bool](repeating: false, count: w)
    for x in 0..<w {
        let bg = Int(v[bgRow * w + x])
        guard bg >= 226 && bg <= 245 else { continue }   // 不在当前标签的灰底上
        cols[x] = rows.contains { v[$0 * w + x] < 170 }
    }
    // 把深色的列连成一团一团（中间断开 2 点以内算一团）
    var clusters: [(Int, Int)] = []
    var x = 0
    while x < w {
        guard cols[x] else { x += 1; continue }
        var end = x
        var gap = 0
        var k = x + 1
        while k < w && gap <= Int(2 * s) {
            if cols[k] { end = k; gap = 0 } else { gap += 1 }
            k += 1
        }
        clusters.append((x, end))
        x = end + 1
    }
    guard let c = clusters.last(where: { Double($0.1 - $0.0) / s >= 8 && Double($0.1 - $0.0) / s <= 30 }) else { return nil }
    return Double(c.0 + c.1) / 2 / s
}

/// 当前标签上「···」按钮的中心（窗口内坐标）；找不到就退回「右边缘往左 14 点」
func dotsButton(_ tab: CGRect) -> CGPoint {
    let region = CGRect(x: tab.minX + tab.width * 0.4, y: tab.minY, width: tab.width * 0.6 + 60, height: tab.height)
    if let cg = capture(region), let cx = dotsCenterX(cg, ptWidth: Double(region.width)) {
        return CGPoint(x: Double(region.minX) + cx, y: Double(tab.midY))
    }
    return CGPoint(x: Double(tab.maxX) - 14, y: Double(tab.midY))
}

/// 标签刚打开时大小还在变（有动画），连着两次量到一样的才算数
func settledTab(near tab: CGRect) -> CGRect {
    var last = tab
    for _ in 0..<8 {
        usleep(150_000)
        guard let t = activeTab(), abs(t.minX - tab.minX) < 40 else { continue }
        if abs(t.width - last.width) <= 2 && abs(t.minX - last.minX) <= 2 { return t }
        last = t
    }
    return last
}

// MARK: - 鼠标键盘

var lastPos = CGPoint.zero
let src = CGEventSource(stateID: .hidSystemState)

func cursor() -> CGPoint { CGEvent(source: nil)?.location ?? .zero }

/// 你一动鼠标或切走微信，就暂停；鼠标甩到主屏幕左上角，就彻底停
func checkUser() throws {
    if stopRequested != 0 { throw StopError(reason: "收到停止指令，已停止", byUser: true) }
    var c = cursor()
    if c.x < 8 && c.y < 8 { throw StopError(reason: "你把鼠标甩到了屏幕左上角，已彻底停止", byUser: true) }
    if hypot(c.x - lastPos.x, c.y - lastPos.y) > 25 {
        usleep(150_000)   // 刚发出的移动可能还没生效，等一下再看
        c = cursor()
    }
    if hypot(c.x - lastPos.x, c.y - lastPos.y) > 25 { throw Interrupted(reason: "你动了鼠标") }
    if !wechatIsFront() { throw Interrupted(reason: "微信被切到后面了") }
}

/// 等你停手：键盘鼠标都静止 5 秒再继续
func waitForIdle() throws {
    let anyInput = CGEventType(rawValue: ~0)!
    while true {
        if stopRequested != 0 { throw StopError(reason: "收到停止指令，已停止", byUser: true) }
        let c = cursor()
        if c.x < 8 && c.y < 8 { throw StopError(reason: "你把鼠标甩到了屏幕左上角，已彻底停止", byUser: true) }
        if CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput) >= 5 { return }
        usleep(300_000)
    }
}

func move(_ x: Double, _ y: Double) throws {
    try checkUser()
    if let w = findWindow() { win = w }
    let p = CGPoint(x: win.minX + x, y: win.minY + y)
    CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
    lastPos = p
}

func click(_ x: Double, _ y: Double) throws {
    try move(x, y)
    pause(0.08, 0.15)
    let p = lastPos
    CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
    usleep(60_000)
    CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
}

/// 往下滚 pixels 点；pixels 为负数就往上滚
func scrollDown(_ x: Double, _ y: Double, _ pixels: Int, step: Int = 40) throws {
    try move(x, y)
    usleep(100_000)
    var done = 0
    while done < abs(pixels) {
        let d = min(step, abs(pixels) - done)
        CGEvent(scrollWheelEvent2Source: src, units: .pixel, wheelCount: 1, wheel1: Int32(pixels > 0 ? -d : d), wheel2: 0, wheel3: 0)?.post(tap: .cghidEventTap)
        done += d
        usleep(16_000)
    }
}

func pressKey(_ code: CGKeyCode) {
    CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: true)?.post(tap: .cghidEventTap)
    CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: false)?.post(tap: .cghidEventTap)
}

func pressEscape() { pressKey(53) }

/// 主页的分栏：「全部」后面跟着「贴图 / 文章 / 视频号」里的几个（各个号不一样，有的只有「全部 文章」），
/// 认字时可能连成一段「全部 文章」，也可能分开；或者在最顶上能看到「xx篇原创内容」
func isProfilePage(_ boxes: [TextBox]) -> Bool {
    let top = boxes.filter { $0.rect.minY < 420 }
    let others = ["文章", "视频号", "贴图"]
    let tabsRow = top.contains { a in
        a.text.contains("全部") && (others.contains { a.text.contains($0) }
            || top.contains { b in others.contains { b.text.contains($0) } && abs(b.rect.midY - a.rect.midY) < 10 && b.rect.minX > a.rect.minX })
    }
    return tabsRow || top.contains { $0.text.contains("篇原创") }
}

/// 确认停在公众号主页，返回主页标签位置和整屏认字结果；开着文章（比如误点开的）就先关掉
var lastProfileTab: CGRect?   // 上一次确认过的公众号主页标签，用来认出右边多开着的文章

func ensureProfile() throws -> (tab: CGRect, boxes: [TextBox]) {
    var noTab = 0
    for _ in 0..<6 {
        try refreshWindow()
        guard let tab = activeTab() else {
            // 刚切回来、窗口还在动画，或者有菜单挡着：收起菜单、把窗口拉到前面再看几次
            noTab += 1
            if noTab >= 3 { throw StopError(reason: "认不出微信的标签栏：请确认微信停在公众号主页；如果电脑是深色模式，请先切到浅色模式") }
            pressEscape()
            bringWeChatFront()
            usleep(800_000)
            continue
        }
        let boxes = snapshot()
        if isProfilePage(boxes) { lastProfileTab = tab; return (tab, boxes) }
        let top = boxes.filter { $0.rect.minY < 420 }
        // 文章页标题下面有「2026年10月15日」这样的日期；视频文章顶上是视频，看不到日期，就看它是不是开在主页标签右边
        let hasDate = top.contains { $0.rect.minY < 200 && $0.text.range(of: "\\d{4}年\\d{1,2}月\\d{1,2}日", options: .regularExpression) != nil }
        let rightOfProfile = lastProfileTab.map { tab.minX > $0.minX + 60 } ?? false
        if (hasDate && tab.minX > 150) || rightOfProfile {
            log("发现开着一篇文章，先关掉它")
            try move(Double(tab.minX) + 15, Double(tab.midY))
            usleep(250_000)
            try click(Double(tab.minX) + 15, Double(tab.midY))
            usleep(1_000_000)
        } else {
            pressEscape()   // 可能有菜单没收起来
            usleep(600_000)
        }
    }
    throw StopError(reason: "当前页面不像公众号主页，已停下（请切回公众号主页后再运行）")
}

/// 新开一个文件从头采：先翻回主页最顶上（看到「xx篇原创内容」那行为止；「内」有时会认成繁体，只认「篇原创」）
func scrollToTop() throws {
    _ = try ensureProfile()
    func atTop() -> Bool {
        snapshot(CGRect(x: 0, y: 0, width: win.width, height: win.height * 0.5)).contains { $0.text.contains("篇原创") }
    }
    if atTop() { return }
    try checkUser()
    pressKey(115)   // Home 键，网页一般直接回顶
    usleep(800_000)
    for _ in 0..<80 {
        if atTop() { return }
        try scrollDown(Double(win.width) * 0.5, Double(win.height) * 0.55, -6000, step: 300)
        usleep(300_000)
    }
    throw StopError(reason: "翻不回主页顶上，已停下")
}

// MARK: - 页面结构

struct Card { let key: String; let title: String; let target: CGPoint; let lines: [String] }   // lines：卡片里每行字，用来核对打开的对不对

/// 标题只留字母数字取前 18 个；认字常把 I 认成 l，统一成 i
func normalize(_ s: String) -> String {
    String(s.lowercased().replacingOccurrences(of: "l", with: "i")
        .unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }.map(Character.init)).prefix(18).description
}

/// 卡片顶上的日期：「今天」「昨天」「星期一」「9月23日」「2025年12月3日」
func isDateLabel(_ s: String) -> Bool {
    let t = s.trimmingCharacters(in: .whitespaces)
    return t == "今天" || t == "昨天"
        || t.range(of: "^(星期[一二三四五六日天]|(\\d{4}年)?\\d{1,2}月\\d{1,2}日)$", options: .regularExpression) != nil
}

/// 把同一行的几段字按左右顺序拼起来（认字常把一行标题拆成几段，比如「一夜三连发，」「Claude Opus 5.5、」）
func rowText(_ boxes: [TextBox], _ first: TextBox, left: CGFloat) -> String {
    boxes.filter { abs($0.rect.midY - first.rect.midY) < 6 && $0.rect.minX >= left - 15 && $0.rect.minX < left + 420 }
        .sorted { $0.rect.minX < $1.rect.minX }.map { $0.text }.joined()
}

/// 列表里完整可见的卡片，以「阅读…」那一行为锚点：
/// - 普通文章：标题在阅读数正上方
/// - 文字帖：阅读数上面是「展开」，标题是日期下面第一行
/// 返回认出的卡片，和完整露出来却认不出标题的卡片（可能会漏）
/// 可点区域：顶上吸住的「全部 贴图 文章 视频号」下面 20 点，到窗口底 8 点
func clickableRange(_ boxes: [TextBox]) -> (top: Double, bottom: Double) {
    let sticky = boxes.filter { ["全部", "文章", "视频号", "贴图"].contains(where: $0.text.contains) && $0.rect.minY < 160 }
    return ((sticky.map { Double($0.rect.maxY) }.max() ?? 25) + 20, Double(win.height) - 8)
}

/// 这一屏处理完以后往下翻多少：让最下面那张完整露出来的卡片，翻完后阅读数停在可点区域顶上 40 点处。
/// 下一张卡片的标题一定在它下面，不会被翻过头；卡片高矮不一（图文、文字帖）也能自适应
func plannedScroll(_ boxes: [TextBox]) -> (points: Double, anchor: TextBox?) {
    let (top, bottom) = clickableRange(boxes)
    let visible = bottom - top
    let stats = boxes.filter { $0.text.hasPrefix("阅读") && Double($0.rect.maxY) <= bottom && Double($0.rect.minY) > top }
    guard let low = stats.max(by: { $0.rect.maxY < $1.rect.maxY }) else { return (visible * 0.5, nil) }
    return (min(max(Double(low.rect.maxY) - (top + 40), 80), visible * 0.9), low)
}

func visibleCards(_ boxes: [TextBox]) -> (cards: [Card], unreadable: [String]) {
    let top = clickableRange(boxes).top
    var cards: [Card] = [], unreadable: [String] = []
    for stat in boxes where stat.text.hasPrefix("阅读") && Double(stat.rect.maxY) <= Double(win.height) - 8 && Double(stat.rect.minY) > top {
        let left = stat.rect.minX
        // 同一张卡片里、阅读数上面的字，从近到远
        var above = boxes.filter { abs($0.rect.minX - left) < 15 && $0.rect.maxY <= stat.rect.minY + 2 && $0.rect.minY >= stat.rect.minY - 420 }
            .sorted { $0.rect.minY > $1.rect.minY }
        // 再往上碰到别的卡片（另一个阅读数）就截止
        if let i = above.firstIndex(where: { $0.text.hasPrefix("阅读") }) { above = Array(above[..<i]) }
        let date = above.first { isDateLabel($0.text) }
        if let d = date { above = above.filter { $0.rect.minY > d.rect.maxY } }

        var title: TextBox?
        var tail = ""   // 标题折成两行、第二行很短（比如「更新」）时，接在第一行后面
        if let near = above.first, near.rect.minY >= stat.rect.minY - 45 {
            let nearText = rowText(boxes, near, left: left)
            if normalize(nearText).count >= 6 {
                title = near
            } else if !nearText.contains("展开") && !nearText.contains("收起") && above.count > 1
                        && above[1].rect.maxY >= near.rect.minY - 12 && normalize(rowText(boxes, above[1], left: left)).count >= 6 {
                title = above[1]
                tail = nearText
            }
        }
        if title == nil && date != nil {
            title = above.last   // 日期下面第一行
        }
        guard let t = title else {
            // 只在整张卡片都露出来（看得到顶上的日期）时才提醒；上半截被挡住的，上一屏已经完整露过
            if date != nil { unreadable.append("\(stat.text)，在窗口第 \(Int(stat.rect.minY)) 点") }
            continue
        }
        guard Double(t.rect.minY) >= top else { continue }   // 标题被顶上挡住了，上一屏已经完整露过
        let text = rowText(boxes, t, left: left) + tail
        guard normalize(text).count >= 4 else {
            unreadable.append("\(stat.text)，在窗口第 \(Int(stat.rect.minY)) 点")
            continue
        }
        let lines = above.map { normalize(rowText(boxes, $0, left: left)) }.filter { $0.count >= 4 }
        cards.append(Card(key: normalize(text), title: text, target: CGPoint(x: t.rect.minX + min(t.rect.width / 2, 120), y: t.rect.midY), lines: lines))
    }
    return (cards.sorted { $0.target.y < $1.target.y }, unreadable)
}

func editDistance(_ a: String, _ b: String) -> Int {
    let a = Array(a), b = Array(b)
    if a.isEmpty || b.isEmpty { return max(a.count, b.count) }
    var row = Array(0...b.count)
    for i in 1...a.count {
        var prev = row[0]; row[0] = i
        for j in 1...b.count {
            let cur = row[j]
            row[j] = a[i - 1] == b[j - 1] ? prev : min(prev, row[j], row[j - 1]) + 1
            prev = cur
        }
    }
    return row[b.count]
}

/// 标签上的标题（会被截断）和卡片是不是同一篇：对得上卡片里任意一行就算（文字帖的标签标题不一定是第一行）
func sameArticle(_ tabText: String, _ card: Card) -> Bool {
    let t = String(normalize(tabText).prefix(6))
    guard t.count >= 4 else { return false }
    return ([card.key] + card.lines).contains { editDistance(t, String($0.prefix(t.count))) <= 1 }
}

// MARK: - 记录

/// 按列表顺序记下每张点过的卡片：「标题」+ 链接（没拿到链接的留空）。md 由它按顺序生成，补漏补到的会插回原位
/// keys：这篇文章认出来过的各种写法（同一张卡片停在不同位置，认出来的字会有出入），文件里用 | 分开
struct Entry {
    var keys: [String]; var url: String; var title = ""   // title：认出来的标题，界面上显示用
    var key: String { keys.first ?? "" }
}
var entries: [Entry] = []
var mdOnly: [String] = []   // md 里有、记录里没有的链接（比如你手动加的），保留在最前面
var angleStyle = false      // md 被编辑器整理成了「<链接>  」一行一条（Markdown 里才会分行显示），写回时保持这个格式

/// 一行里的网址：不管外面套了尖括号、列表符号还是 Markdown 链接
func extractURL(_ line: Substring) -> String? {
    guard let r = line.range(of: #"https?://[^\s<>()\[\]"]+"#, options: .regularExpression) else { return nil }
    return String(line[r])
}

func loadRecords() {
    mdPath = "\(dataDir)/\(mdName!)"
    statePath = mdPath + ".已点过.txt"
    entries = ((try? String(contentsOfFile: statePath, encoding: .utf8)) ?? "").split(separator: "\n").map {
        let f = $0.split(separator: "\t", maxSplits: 2, omittingEmptySubsequences: false)
        return Entry(keys: f[0].split(separator: "|").map(String.init).filter { !isPlaceholder($0) },
                     url: f.count > 1 ? String(f[1]) : "", title: f.count > 2 ? String(f[2]) : "")
    }
    var merged: [Entry] = []
    var firstAt: [String: Int] = [:]
    for var e in entries {
        if !isPlaceholder(e.title) {
            let k = normalize(e.title)
            if k.count >= 4 && !e.keys.contains(k) { e.keys.append(k) }
        }
        if !e.url.isEmpty, let i = firstAt[e.url] {
            for k in e.keys where !merged[i].keys.contains(k) { merged[i].keys.append(k) }
            if isPlaceholder(merged[i].title) && !isPlaceholder(e.title) { merged[i].title = e.title }
            continue
        }
        if !e.url.isEmpty { firstAt[e.url] = merged.count }
        merged.append(e)
    }
    entries = merged
    let md = (try? String(contentsOfFile: mdPath, encoding: .utf8)) ?? ""
    angleStyle = md.split(separator: "\n").contains { $0.hasPrefix("<http") }
    let known = Set(entries.map(\.url))
    mdOnly = md.split(separator: "\n").compactMap(extractURL).filter { !known.contains($0) }
    urls = Set(entries.map(\.url).filter { !$0.isEmpty } + mdOnly)
}

/// 公众号名字：主页标签上的字（去掉左边图标被认成的字母）
func accountName(_ tab: CGRect) -> String {
    let t = snapshot(CGRect(x: tab.minX + 22, y: tab.minY, width: max(tab.width - 52, 40), height: tab.height)).map { $0.text }.joined()
    // 左边的图标常被认成标点或一个字母，比如「）数字生命卡兹克」「v 数字生命卡兹克」
    let cleaned = t.replacingOccurrences(of: "^[^\\p{Han}A-Za-z0-9]+", with: "", options: .regularExpression)
        .replacingOccurrences(of: "^[A-Za-z•·]\\s+", with: "", options: .regularExpression)
    return cleaned.components(separatedBy: CharacterSet(charactersIn: "/\\:*?\"<>|.").union(.newlines)).joined()
        .trimmingCharacters(in: .whitespaces)
}

/// 按公众号名字找文件：已有名字差不多的（认字差一个字）就接着用它，没有就新建
func pickMdName(_ name: String) -> String {
    guard normalize(name).count >= 2 else { return "文章链接.md" }
    let files = ((try? FileManager.default.contentsOfDirectory(atPath: dataDir)) ?? []).filter { $0.hasSuffix(".md") && $0 != "使用说明.md" }
    let n = normalize(name)
    if let hit = files.first(where: {
        let s = normalize(String($0.dropLast(3)))
        return s == n || (min(s.count, n.count) >= 4 && editDistance(s, n) <= 1)
    }) { return hit }
    return name + ".md"
}

/// 点过没有：标题一样，或者一个是另一个的开头（认字时标题被截短）；
/// 第一遍还容忍认错一两个字，少白开几次。补漏那遍只认这两种，宁可多白开（链接去重兜底），不放过漏掉的
var strictSeen = false
func seenIndex(_ key: String) -> Int? {
    entries.firstIndex { e in e.keys.contains { old in
        if old == key { return true }
        let n = min(old.count, key.count)
        if n >= 10 && (old.hasPrefix(key) || key.hasPrefix(old)) { return true }
        if strictSeen { return false }
        return n >= 12 && abs(old.count - key.count) <= 2 && editDistance(old, key) <= (n >= 15 ? 2 : 1)
    } }
}

/// 扫到列表哪儿了：上一张经过的卡片在 entries 里的位置。-1 表示从主页顶上开始，nil 表示接着文件末尾
var cursorIndex: Int?

/// 记下一篇：链接已经记过（同一篇换了个叫法又点开了），就把这个叫法补进去，下次不再点开；
/// 没记过就插在上一张经过的卡片后面。然后按顺序重写 md
func record(_ key: String, _ url: String, _ title: String) {
    let clean = title.replacingOccurrences(of: "\t", with: " ")
    if !url.isEmpty, let i = entries.firstIndex(where: { $0.url == url }) {
        if !key.isEmpty && !entries[i].keys.contains(key) { entries[i].keys.append(key) }
        if entries[i].title.isEmpty || isPlaceholder(entries[i].title) { entries[i].title = clean }
        cursorIndex = i
    } else {
        let at = min((cursorIndex ?? entries.count - 1) + 1, entries.count)
        entries.insert(Entry(keys: key.isEmpty ? [] : [key], url: url, title: clean), at: at)
        cursorIndex = at
    }
    if !url.isEmpty { urls.insert(url) }
    try? entries.map { "\($0.keys.joined(separator: "|"))\t\($0.url)\t\($0.title)" }.joined(separator: "\n")
        .write(toFile: statePath, atomically: true, encoding: .utf8)
    var written = Set<String>()
    let lines = (mdOnly + entries.map(\.url)).filter { !$0.isEmpty && written.insert($0).inserted }
        .map { angleStyle ? "<\($0)>  " : $0 }
    try? (lines.joined(separator: "\n") + "\n").write(toFile: mdPath, atomically: true, encoding: .utf8)
}

// MARK: - 一篇文章：点开 → ··· → 复制链接 → 关掉

/// 刚点开文章时，标签先显示微信图标和「微信公众平台」（常被截成「微信…」），加载完才换成文章标题；
/// 这之前菜单里也没有「复制链接」
func isPlaceholder(_ tabText: String) -> Bool {
    let n = normalize(tabText)
    return n.isEmpty || n == "微信" || n.contains("微信公众平") || n.hasPrefix("正在加") || tabText.contains("加载")
}

let loadTimeout = 25.0   // 网络慢时，等一篇文章加载最多等这么久

struct Result {
    let url: String?
    let loaded: Bool       // 标签换成了文章标题
    let matched: Bool      // 打开的就是要点的那张卡片
    var opened = ""        // 实际打开的那篇：标签标题（认字后的）
    var openedTitle = ""
}

func copyLink(of card: Card, profileTab: CGRect) throws -> Result {
    // 点卡片，等文章在主页标签右边打开成新的当前标签（标签一多会自动变窄，只能比左边起点）。
    // 第一下点击有时只是把微信窗口激活了，没反应就再点一次
    var opened: CGRect?
    clicks: for attempt in 0..<2 {
        try click(Double(card.target.x), Double(card.target.y))
        for _ in 0..<(attempt == 0 ? 13 : 20) {
            usleep(200_000)
            if let t = activeTab(), t.minX > profileTab.minX + 60 { opened = t; break clicks }
        }
    }
    guard var tab = opened else {
        pressEscape()
        log("⚠️ 点了没打开新标签，跳过：\(card.title)")
        return Result(url: nil, loaded: false, matched: false)
    }

    tab = settledTab(near: tab)

    // 等加载完：标签从「微信…」换成文章标题
    let titleArea = CGRect(x: tab.minX + 20, y: tab.minY, width: tab.width - 60, height: tab.height)
    let t0 = Date()
    var tabText = ""
    var toldSlow = false
    while true {
        tabText = snapshot(titleArea).map { $0.text }.joined()
        if !isPlaceholder(tabText) { break }
        let waited = Date().timeIntervalSince(t0)
        if waited > loadTimeout { break }
        if waited > 4 && !toldSlow {
            log("网络有点慢，等文章加载出来…")
            toldSlow = true
            saveStatus(["state": "running"])
        }
        usleep(250_000)
    }
    let loaded = !isPlaceholder(tabText)
    let matched = loaded && sameArticle(tabText, card)
    if loaded && !matched { log("⚠️ 打开的文章和要点的对不上（标签：\(tabText)，卡片：\(card.title)）") }

    // 点「···」找「复制链接」；还没有就收起菜单等一会儿再点，一直等到加载超时
    let loadWait = Date().timeIntervalSince(t0)
    var url: String?
    var attempt = 0
    while url == nil {
        // 每次点之前重新在标签上找「···」：按推算的位置点，标签量偏了会点到旁边的「停止 / 刷新」
        let dots = dotsButton(tab)
        try click(Double(dots.x), Double(dots.y))
        attempt += 1
        var item: TextBox?
        for _ in 0..<5 {
            usleep(200_000)
            item = snapshot(CGRect(x: Double(dots.x) - 240, y: Double(tab.maxY), width: 320, height: 170)).first { $0.text.contains("复制链接") }
            if item != nil { break }
        }
        if let it = item {
            let before = NSPasteboard.general.changeCount
            try click(Double(it.rect.midX), Double(it.rect.midY))
            for _ in 0..<20 where NSPasteboard.general.changeCount == before { usleep(100_000) }
            if NSPasteboard.general.changeCount != before,
               let s = NSPasteboard.general.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines),
               s.hasPrefix("https://mp.weixin.qq.com/") {
                url = s
            }
            break
        }
        pressEscape()
        if Date().timeIntervalSince(t0) > loadTimeout + 5 { break }
        usleep(800_000)
    }
    if url != nil && (loadWait > 3 || attempt > 1) {
        log(String(format: "这篇慢：等加载 %.1f 秒，点了 %d 次「···」", loadWait, attempt))
    }
    if url == nil {
        log(loaded ? "⚠️ 没拿到链接：\(card.title)" : "⚠️ 等了 \(Int(loadTimeout)) 秒还没加载出来，先跳过，补漏时再试：\(card.title)")
    }

    // 点标签左边的「✕」关掉文章（鼠标要先停上去它才出来），确认当前标签回到公众号主页
    for _ in 0..<3 {
        try move(Double(tab.minX) + 15, Double(tab.midY))
        usleep(250_000)
        try click(Double(tab.minX) + 15, Double(tab.midY))
        for _ in 0..<8 {
            usleep(200_000)
            if let t = activeTab(), t.minX < tab.minX - 60 {
                return Result(url: url, loaded: loaded, matched: matched, opened: loaded ? normalize(tabText) : "", openedTitle: loaded ? tabText : "")
            }
        }
    }
    throw StopError(reason: "文章标签关不掉，已停下（请手动关掉后再运行）")
}

// MARK: - 主流程

let savedClipboard = NSPasteboard.general.string(forType: .string)
func restoreClipboard() {
    guard let s = savedClipboard else { return }
    NSPasteboard.general.clearContents()
    NSPasteboard.general.setString(s, forType: .string)
}


/// 一屏一屏往下扫，没点过的卡片挨个采；扫到底返回 true，到了本次上限返回 false
func sweep() throws -> Bool {
    var sameCount = 0
    var lastSignature = ""
    var skipped = Set<String>()     // 这一遍里加载超时、没拿到链接的，不在这一遍反复点，留给补漏
    var openedBy: [String: [String]] = [:]   // 每张卡片点开后拿到过哪些链接
    var opensHere = 0                         // 这一屏（上次翻页以后）点开了几次
    var scrollRatio = 1.0           // 微信页面实际移动的距离 ÷ 滚轮滚的距离，按上一次翻页的结果校准
    var anchor: (text: String, x: CGFloat, y: CGFloat, asked: Double)?   // 上次翻页前最下面那张卡片的阅读数
    var backtracks = 0
    while true {
        if added >= maxCount { return false }
        try refreshWindow()
        let (profileTab, boxes) = try ensureProfile()
        // 翻页前最下面那张卡片的阅读数，翻完应该停在可点区域靠上的位置。
        // 找不到它，说明翻过头了：紧挨在它下面的那张卡片，标题可能被翻到顶上挡住了，会漏掉。往回拨一点再看
        if let a = anchor {
            let top = clickableRange(boxes).top
            let key = normalize(a.text)
            let found = boxes.first {
                Double($0.rect.minY) >= top - 5 && abs($0.rect.minX - a.x) < 15
                    && $0.text.hasPrefix("阅读") && editDistance(normalize($0.text), key) <= 1
            }
            if let b = found {
                backtracks = 0
                // 校准滚动比例：宁可估大（翻得少、多看一屏），不能估小（翻过头）。所以往大了改得快，往小了改得慢
                let moved = Double(a.y - b.rect.minY)
                if a.asked > 0 && moved > 20 {
                    let r = moved / a.asked
                    scrollRatio = r > scrollRatio ? 0.3 * scrollRatio + 0.7 * r : 0.8 * scrollRatio + 0.2 * r
                    scrollRatio = min(max(scrollRatio, 0.5), 2.5)
                }
            } else if backtracks < 4 {
                backtracks += 1
                scrollRatio = min(scrollRatio * 1.3, 2.5)
                log("翻过头了（上一屏最下面那篇不见了），往回拨一点，免得漏掉顶上的文章")
                let visible = clickableRange(boxes).bottom - top
                try scrollDown(Double(a.x) + 100, Double(win.height) * 0.55, -Int(visible * 0.35))
                anchor = (a.text, a.x, a.y, 0)   // 继续找它，这次不拿来校准
                pause(0.7, 1.0)
                continue
            }
        }
        anchor = nil
        let (cards, notRead) = visibleCards(boxes)
        for u in notRead where unreadable.insert(u).inserted {
            log("⚠️ 有张卡片认不出标题（\(u)），可能会漏，跑完补漏时会再看一次")
        }

        // 关掉文章后列表停在原位，这一屏的卡片按位置挨个点，不用每篇都重新认整屏
        var redo = false
        for card in cards {
            if let i = seenIndex(card.key) { cursorIndex = i; continue }   // 点过的：只记下扫到这儿了
            if skipped.contains(card.key) { continue }
            if added >= maxCount { return false }
            // 兜底：一屏最多三四张卡片，点开 10 次还没走完，说明卡在哪张怪卡片上了。
            // 这一屏剩下的先跳过、往下翻，保证一直往下走；跳过的留给最后从头补漏那一遍
            if opensHere >= 10 {
                let left = cards.filter { seenIndex($0.key) == nil && !skipped.contains($0.key) }
                left.forEach { skipped.insert($0.key) }
                log("⚠️ 这一屏点了 \(opensHere) 次还没走完，剩下 \(left.count) 张先跳过、接着往下翻，最后补漏时再试")
                break
            }
            opensHere += 1
            sameCount = 0
            let t0 = Date()
            let r = try copyLink(of: card, profileTab: profileTab)
            guard let u = r.url else {
                skipped.insert(card.key)
                continue
            }
            let isNew = !urls.contains(u)
            openedBy[card.key, default: []].append(u)
            // 同一张卡片点开两次拿到的都是这个链接，就是它了（标签标题认错字、或者和卡片写法不一样时，靠这个收住，不会一直重开）
            let sameTwice = openedBy[card.key]!.filter { $0 == u }.count >= 2
            if r.matched || !r.loaded || sameTwice {
                // 打开的就是这张（标签一直没换成标题时，相信点的位置）
                record(card.key, u, card.title)
            } else {
                // 点开的是别的卡片（列表位置变了）：按实际打开的那篇记，这一屏重新认
                record(r.opened, u, r.openedTitle)
                redo = true
                if openedBy[card.key]!.count >= 3 {
                    skipped.insert(card.key)
                    log("⚠️ 这张卡片点了 3 次都对不上，先跳过，补漏时再试：\(card.title)")
                }
            }
            if isNew {
                added += 1
                if recovering { recovered += 1 }
                status["last_title"] = r.matched || !r.loaded ? card.title : r.openedTitle
                durations.append(Date().timeIntervalSince(t0))
                log(String(format: "%@ 第 %d 条（%.1f 秒）：%@  %@", recovering ? "🔁 补漏" : "✅", urls.count, durations.last!,
                           (r.matched || !r.loaded ? card.title : r.openedTitle) as NSString, u as NSString))
                if added % restEvery == 0 { log("歇一会儿…"); pause(15, 30) }
            } else {
                reopened += 1
                log("重复，跳过（链接已经有了，不会重复写）：\(card.title)")
            }
            if redo { break }
            pause(0.2, 0.4)
        }
        if redo { continue }

        // 往下翻。翻完画面没变，可能在加载：越等越久，中间往回拨一点再往下催它加载，四次都不动才算到底
        let column = cards.first.map { Double($0.target.x) } ?? Double(win.width) * 0.5
        let signature = boxes.filter { Double($0.rect.minY) > Double(win.height) * 0.3 }.map { $0.text }.joined()
        if signature == lastSignature {
            sameCount += 1
            if sameCount > 4 {
                let bottom = boxes.sorted { $0.rect.minY < $1.rect.minY }.suffix(2).map { $0.text }.joined(separator: " / ")
                log("连续 4 次往下翻都没有新内容，判断已经到底（最后一屏底部：\(bottom)）")
                return true
            }
            let wait = [2.0, 3.0, 5.0, 8.0][sameCount - 1]
            log("往下翻没出新内容，可能在加载，等 \(Int(wait)) 秒再试")
            pause(wait, wait + 0.5)
            if sameCount >= 2 { try scrollDown(column, Double(win.height) * 0.55, -300); usleep(600_000) }
        } else {
            sameCount = 0
        }
        lastSignature = signature
        let plan = plannedScroll(boxes)
        let ask = plan.points * 0.9 / scrollRatio   // 少翻一点：翻不够只是多看一屏，翻过头会漏
        if let a = plan.anchor { anchor = (a.text, a.rect.minX, a.rect.minY, ask) }
        try scrollDown(column, Double(win.height) * 0.55, Int(ask))
        opensHere = 0
        pause(0.7, 1.0)
    }
}

/// 测试用：--test-dots <标签右半边截图.png> <这张图对应的点宽>，打印找到的「···」中心
if let i = CommandLine.arguments.firstIndex(of: "--test-dots"), i + 2 < CommandLine.arguments.count {
    let img = NSImage(contentsOfFile: CommandLine.arguments[i + 1])!
    var r = CGRect(origin: .zero, size: img.size)
    let cg = img.cgImage(forProposedRect: &r, context: nil, hints: nil)!
    print("「···」中心离图左边：", dotsCenterX(cg, ptWidth: Double(CommandLine.arguments[i + 2])!).map { String(format: "%.1f 点", $0) } ?? "没找到")
    exit(0)
}

/// 测试用：--test-cards <文件>，从文件读一屏认出的字（第一行「WIN<TAB>宽<TAB>高」，之后每行「x y w h 文字」用 TAB 分开），
/// 打印认出的卡片和翻页计划；和 Windows 版的同名测试比对，保证两边规则一样
if let i = CommandLine.arguments.firstIndex(of: "--test-cards"), i + 1 < CommandLine.arguments.count {
    let lines = ((try? String(contentsOfFile: CommandLine.arguments[i + 1], encoding: .utf8)) ?? "").split(separator: "\n")
    var boxes: [TextBox] = []
    for l in lines {
        let f = l.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
        if f.first == "WIN", f.count >= 3 { win = CGRect(x: 0, y: 0, width: Double(f[1])!, height: Double(f[2])!); continue }
        guard f.count >= 5, let x = Double(f[0]), let y = Double(f[1]), let w = Double(f[2]), let h = Double(f[3]) else { continue }
        boxes.append(TextBox(text: f[4...].joined(separator: "\t"), rect: CGRect(x: x, y: y, width: w, height: h)))
    }
    print("PROFILE\t\(isProfilePage(boxes) ? "是" : "否")")
    let (cards, notRead) = visibleCards(boxes)
    for c in cards { print("CARD\t\(c.key)\t\(c.title)\t\(Int(c.target.x.rounded()))\t\(Int(c.target.y.rounded()))") }
    for u in notRead { print("UNREAD\t\(u.components(separatedBy: "，").first ?? u)") }
    let (top, bottom) = clickableRange(boxes)
    let plan = plannedScroll(boxes)
    print("RANGE\t\(Int(top.rounded()))\t\(Int(bottom.rounded()))")
    print("PLAN\t\(Int(plan.points.rounded()))\t\(plan.anchor?.text ?? "")")
    exit(0)
}

/// 自检（不碰微信）：--self-test [--dir 目录 --file 名字.md]，检查「加载中」的认法，以及读记录时合并重复
if CommandLine.arguments.contains("--self-test") {
    for t in ["正在加载", "正在加毂", "正在加穀", "）彳微信公众平台", "1微信公众平台", "微信…", "聊聊DeepSeek V4正式版，真正的智能平权。", "正在发生的AI革命"] {
        print(isPlaceholder(t) ? "加载中" : "是标题", "←", t)
    }
    if mdName != nil {
        let raw = ((try? String(contentsOfFile: "\(dataDir)/\(mdName!).已点过.txt", encoding: .utf8)) ?? "").split(separator: "\n").count
        loadRecords()
        print("记录文件 \(raw) 行 → 合并后 \(entries.count) 篇，不重复的链接 \(Set(entries.map(\.url)).count) 个")
        for (i, e) in entries.enumerated() where e.keys.isEmpty || isPlaceholder(e.title) { print("第 \(i + 1) 篇没有叫法或标题：", e.url, e.title) }
    }
    exit(0)
}

/// 检查模式：只看不点，打印认到的标签、文字和卡片
if probeMode {
    bringWeChatFront()
    try? refreshWindow()
    print("窗口:", win)
    let tab = activeTab()
    print("当前标签:", tab as Any, "公众号名字:", tab.map(accountName) ?? "")
    let boxes = snapshot()
    for b in boxes { print(String(format: "(%4.0f,%4.0f %3.0fx%2.0f) ", b.rect.minX, b.rect.minY, b.rect.width, b.rect.height) + b.text) }
    print("---- 认出的卡片 ----")
    print("是公众号主页:", isProfilePage(boxes) ? "是" : "否")
    let (cards, notRead) = visibleCards(boxes)
    for c in cards { print(Int(c.target.x), Int(c.target.y), c.title) }
    for u in notRead { print("认不出标题：", u) }
    let (top, bottom) = clickableRange(boxes)
    let plan = plannedScroll(boxes)
    print("可点区域: \(Int(top))-\(Int(bottom))  计划翻页: \(Int(plan.points)) 点  锚点: \(plan.anchor?.text ?? "无")")
    exit(0)
}

/// 被你打断（动鼠标、切走微信）就暂停，等你停手 5 秒后切回微信接着做
func resumable<T>(_ body: () throws -> T) throws -> T {
    while true {
        do { return try body() } catch let e as Interrupted {
            saveStatus(["state": "paused"])
            log("⏸ \(e.reason)，暂停。键盘鼠标静止 5 秒后自动继续（要彻底停止：把鼠标甩到主屏幕左上角）")
            try waitForIdle()
            saveStatus(["state": "running"])
            log("▶️ 继续")
            bringWeChatFront()
            try refreshWindow()
            lastPos = cursor()
        }
    }
}

/// 缺哪些权限（第一次运行会弹出系统授权提示）
func missingPermissions() -> [String] {
    var missing: [String] = []
    let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    if !AXIsProcessTrustedWithOptions(opts) { missing.append("辅助功能") }
    if !CGPreflightScreenCaptureAccess() { _ = CGRequestScreenCaptureAccess(); missing.append("屏幕录制") }
    return missing
}

// 同一时间只跑一个
if let s = try? String(contentsOfFile: pidPath, encoding: .utf8), let p = Int32(s.trimmingCharacters(in: .whitespacesAndNewlines)),
   p != getpid(), kill(p, 0) == 0 {
    print("已经有一个在跑了（进程 \(p)），这次不启动")
    exit(0)
}
try? "\(getpid())".write(toFile: pidPath, atomically: true, encoding: .utf8)
signal(SIGTERM) { _ in stopRequested = 1 }
signal(SIGINT) { _ in stopRequested = 1 }
saveStatus(["state": "starting", "message": "正在启动"])

var finalState = "finished"
let startTime = Date()
do {
    let missing = missingPermissions()
    if !missing.isEmpty {
        finalState = "needs_permission"
        throw StopError(reason: "缺少权限：请在 系统设置 → 隐私与安全性 → \(missing.joined(separator: "、")) 里打开「公众号链接采集」，然后再启动一次")
    }
    if wechatPIDs.isEmpty { throw StopError(reason: "微信没有打开：请先打开微信，进入要采的公众号主页，再启动") }
    saveStatus(["state": "waiting_idle"])
    log("等你松开鼠标键盘 5 秒后开始…")
    try waitForIdle()
    saveStatus(["state": "running"])
    let profileTab = try resumable { () -> CGRect in
        bringWeChatFront()
        try refreshWindow()
        lastPos = cursor()
        return try ensureProfile().tab
    }
    account = accountName(profileTab)
    if mdName == nil { mdName = pickMdName(account) }
    loadRecords()
    log("开始采集「\(account)」→ \(mdPath)（已有 \(urls.count) 条，已点过 \(entries.count) 篇）。误碰鼠标会暂停、停手 5 秒自动继续；鼠标甩到主屏幕左上角彻底停止。")
    var reachedEnd = false
    var lookedBack = false
    if !recheckOnly {
        reachedEnd = try resumable { () -> Bool in
            if entries.isEmpty {
                try scrollToTop()
                cursorIndex = -1
            } else if !lookedBack {
                // 接着上次采：先往回翻半屏多一点，上次停下时顶上要是有漏掉的，这次能补上（点过的会直接跳过）
                lookedBack = true
                _ = try ensureProfile()
                log("接着上次采：先往回翻一点，看看停下的地方有没有漏的")
                try scrollDown(Double(win.width) * 0.5, Double(win.height) * 0.55, -Int(Double(win.height) * 0.6))
                pause(0.7, 1.0)
            }
            return try sweep()
        }
    }
    if recheckOnly || reachedEnd {
        log("从头再扫一遍，补漏掉的…")
        recovering = true
        strictSeen = true
        var atTop = false
        let done = try resumable { () -> Bool in
            if !atTop { try scrollToTop(); atTop = true; cursorIndex = -1 }
            return try sweep()
        }
        if done { log("补漏扫完了。") }
    }
    if added >= maxCount { log("达到本次上限 \(maxCount) 篇。") }
} catch let e as StopError {
    log(e.reason)
    status["reason"] = e.reason
    if finalState != "needs_permission" { finalState = e.byUser ? "stopped" : "error" }
} catch {
    log("出错：\(error)")
    status["reason"] = "\(error)"
    finalState = "error"
}
restoreClipboard()
try? FileManager.default.removeItem(atPath: tmpShot)   // 截图里有微信画面，用完删掉
try? FileManager.default.removeItem(atPath: pidPath)
let total = Date().timeIntervalSince(startTime)
let avg = durations.isEmpty ? 0 : durations.reduce(0, +) / Double(durations.count)
log(String(format: "本次新增 %d 条（其中补漏 %d 条），白开 %d 次，认不出标题的卡片 %d 张；用时 %.0f 秒（每篇平均 %.1f 秒），文件里共 %d 条：%@",
           added, recovered, reopened, unreadable.count, total, avg, urls.count, mdPath))
saveStatus(["state": finalState, "finished_at": ISO8601DateFormatter().string(from: Date())])
