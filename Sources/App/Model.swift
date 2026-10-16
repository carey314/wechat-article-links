import AppKit
import ApplicationServices
import CoreGraphics
import SwiftUI

let brandGreen = Color(red: 0.09, green: 0.64, blue: 0.38)
let brandGreenLight = Color(red: 0.25, green: 0.81, blue: 0.55)

enum RunMode: String, CaseIterable, Identifiable {
    case all, trial, recheck
    var id: String { rawValue }
    var label: String {
        switch self {
        case .all: "全部采完"
        case .trial: "先试 10 篇"
        case .recheck: "只补漏"
        }
    }
    var hint: String {
        switch self {
        case .all: "从上次停下的地方接着采，采到底后自动从头再扫一遍，把漏掉的补回原位。"
        case .trial: "只新采 10 篇，先看看效果。再点开始会接着往下采。"
        case .recheck: "从主页顶上扫一遍，只补漏掉的，已经有的不会重复。"
        }
    }
}

enum SidebarItem: Hashable {
    case home
    case setup
    case account(String)   // md 文件名
}

struct Account: Identifiable, Equatable {
    var id: String { file }
    let file: String
    let name: String
    let count: Int
    let modified: Date
}

struct Article: Identifiable, Hashable {
    let id: Int   // 在列表里的序号，从 1 开始
    let title: String
    let url: String
    var shortURL: String { url.replacingOccurrences(of: "https://", with: "") }
}

/// 引擎写在 status.json 里的进度
struct EngineStatus: Equatable {
    var state = "", message = "", account = "", mdPath = "", phase = "", lastTitle = "", reason = "", runID = ""
    var total = 0, added = 0, recovered = 0, reopened = 0, unreadable = 0
    var avg = 0.0

    init() {}
    init(_ d: [String: Any]) {
        func s(_ k: String) -> String { d[k] as? String ?? "" }
        func i(_ k: String) -> Int { (d[k] as? NSNumber)?.intValue ?? 0 }
        state = s("state"); message = s("message"); account = s("account"); mdPath = s("md_path")
        phase = s("phase"); lastTitle = s("last_title"); reason = s("reason"); runID = s("run_id")
        total = i("total_links"); added = i("added_this_run"); recovered = i("recovered")
        reopened = i("reopened"); unreadable = i("unreadable")
        avg = (d["avg_seconds"] as? NSNumber)?.doubleValue ?? 0
    }

    var isActive: Bool { ["starting", "waiting_idle", "running", "paused"].contains(state) }
    var file: String? { mdPath.isEmpty ? nil : (mdPath as NSString).lastPathComponent }
}

/// 引擎输出的一行日志，拆成界面上好看的样子
struct LogLine: Identifiable {
    enum Kind { case collected, recovered, warning, paused, resumed, info }
    let id: Int
    let time: String
    let kind: Kind
    let text: String
    let detail: String
}

/// 一行里的网址：md 被编辑器整理过（套了尖括号、列表符号、Markdown 链接）也能认出来
func extractURL(_ line: Substring) -> String? {
    guard let r = line.range(of: #"https?://[^\s<>()\[\]"]+"#, options: .regularExpression) else { return nil }
    return String(line[r])
}

private let collectedPattern = try! NSRegularExpression(pattern: #"^(✅|🔁 补漏) 第 (\d+) 条（([\d.]+) 秒）：(.*?)\s+(https?://\S+)$"#)

func parseLogLine(_ raw: String, id: Int) -> LogLine {
    var line = raw
    var time = ""
    if line.hasPrefix("["), let close = line.firstIndex(of: "]") {
        time = String(line[line.index(after: line.startIndex)..<close])
        line = String(line[line.index(after: close)...]).trimmingCharacters(in: .whitespaces)
    }
    let ns = line as NSString
    if let m = collectedPattern.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)) {
        let kind: LogLine.Kind = ns.substring(with: m.range(at: 1)).hasPrefix("🔁") ? .recovered : .collected
        return LogLine(id: id, time: time, kind: kind,
                       text: "第 \(ns.substring(with: m.range(at: 2))) 条 · \(ns.substring(with: m.range(at: 4)))",
                       detail: "\(ns.substring(with: m.range(at: 3))) 秒")
    }
    for (prefix, kind) in [("⚠️", LogLine.Kind.warning), ("⏸", .paused), ("▶️", .resumed)] where line.hasPrefix(prefix) {
        return LogLine(id: id, time: time, kind: kind, text: String(line.dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces), detail: "")
    }
    return LogLine(id: id, time: time, kind: .info, text: line, detail: "")
}

@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()
    static let dataDirKey = "dataDir"
    static let wechatBundleID = "com.tencent.xinWeChat"

    @Published var selection: SidebarItem? = .home
    @Published var mode: RunMode = .all
    @Published var menuBarShown = false
    @Published private(set) var axOK = false
    @Published private(set) var screenOK = false
    @Published private(set) var screenRequested = false
    @Published private(set) var wechatRunning = false
    @Published private(set) var darkMode = false
    @Published private(set) var dataDir: URL
    @Published private(set) var accounts: [Account] = []
    @Published private(set) var running = false
    @Published private(set) var stopping = false
    @Published private(set) var status = EngineStatus()
    @Published private(set) var lastRun: EngineStatus?
    @Published private(set) var logs: [LogLine] = []
    @Published private(set) var logsFromLastRun = false   // logs 是打开应用时从日志文件里读出的上一次运行
    @Published private(set) var startedAt: Date?
    @Published private(set) var toast: String?

    var permissionsOK: Bool { axOK && screenOK }
    var missingPermissions: Int { (axOK ? 0 : 1) + (screenOK ? 0 : 1) }
    /// 装在「应用程序」里才稳：在下载文件夹或安装包里直接运行，系统会把它放到临时位置（App Translocation）
    var installedInApplications: Bool {
        let p = Bundle.main.bundlePath
        return p.hasPrefix("/Applications/") || p.hasPrefix(NSHomeDirectory() + "/Applications/")
    }

    /// 给界面用的双向绑定：值真的变了才写。系统控件（菜单栏图标、列表选中项、分段选择器）刷新时会把同样的值写回来，
    /// 直接绑 @Published 会每次都通知所有界面重画，重画又触发写回，应用就卡死在这个循环里
    func binding<T: Equatable>(_ keyPath: ReferenceWritableKeyPath<AppModel, T>) -> Binding<T> {
        Binding(get: { self[keyPath: keyPath] }, set: { if self[keyPath: keyPath] != $0 { self[keyPath: keyPath] = $0 } })
    }
    var canStart: Bool { permissionsOK && wechatRunning && !running }
    /// 主窗口关掉以后，采完时用它重新打开
    var reopenWindow: (() -> Void)?

    private var process: Process?
    private var runID = ""
    private var pending = ""
    private var statusTimer: Timer?
    private var envTimer: Timer?
    private var activity: NSObjectProtocol?
    private var toastTask: Task<Void, Never>?
    private var countCache: [String: (Date, Int)] = [:]
    private var articleCache: [String: (Date, [Article])] = [:]

    /// 数据文件夹：命令行 --dir（调试用）> 设置里选过的 > 文稿/公众号链接采集
    private init() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--dir"), i + 1 < args.count {
            dataDir = URL(fileURLWithPath: args[i + 1])
        } else if let saved = UserDefaults.standard.string(forKey: Self.dataDirKey), !saved.isEmpty {
            dataDir = URL(fileURLWithPath: saved)
        } else {
            dataDir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents/公众号链接采集")
        }
        try? FileManager.default.createDirectory(at: dataDir, withIntermediateDirectories: true)
        refreshEnvironment()
        reloadAccounts()
        loadPreviousResult()
        // 调试用：--debug-select setup 打开后直接显示「设置与权限」页
        if let i = args.firstIndex(of: "--debug-select"), i + 1 < args.count {
            if args[i + 1] == "setup" { selection = .setup }
            if args[i + 1] == "first-account", let a = accounts.first { selection = .account(a.file) }
        }
        // 调试用：--debug-mode trial|recheck|all，启动 2 秒后自动切换采集范围（复现界面问题，不用动鼠标）
        if let i = args.firstIndex(of: "--debug-mode"), i + 1 < args.count, let m = RunMode(rawValue: args[i + 1]) {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { MainActor.assumeIsolated { AppModel.shared.mode = m } }
        }
        envTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { _ in
            MainActor.assumeIsolated { AppModel.shared.tick() }
        }
    }

    private func tick() {
        refreshEnvironment()
        if !running { reloadAccounts() }
    }

    // MARK: 权限、微信、外观

    func refreshEnvironment() {
        let ax = AXIsProcessTrusted()
        let screen = CGPreflightScreenCaptureAccess()
        let wechat = NSWorkspace.shared.runningApplications.contains { $0.bundleIdentifier == Self.wechatBundleID }
        let dark = UserDefaults.standard.string(forKey: "AppleInterfaceStyle") == "Dark"
        if ax != axOK { axOK = ax }
        if screen != screenOK { screenOK = screen }
        if wechat != wechatRunning { wechatRunning = wechat }
        if dark != darkMode { darkMode = dark }
    }

    func requestAccessibility() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(opts)   // 把本应用加进系统设置里的列表
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    func requestScreenRecording() {
        _ = CGRequestScreenCaptureAccess()
        screenRequested = true
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
    }

    func openAccessibilitySettings() { openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") }
    func openScreenSettings() { openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") }

    /// 复制到「应用程序」并从那里重新打开
    func moveToApplications() {
        let dest = URL(fileURLWithPath: "/Applications").appendingPathComponent(Bundle.main.bundleURL.lastPathComponent)
        let fm = FileManager.default
        do {
            if fm.fileExists(atPath: dest.path) { try fm.trashItem(at: dest, resultingItemURL: nil) }
            try fm.copyItem(at: Bundle.main.bundleURL, to: dest)
        } catch {
            flash("没能复制到「应用程序」，请手动把它拖进去")
            return
        }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: dest, configuration: cfg) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }

    func openAppearanceSettings() {
        openSettings("x-apple.systempreferences:com.apple.Appearance-Settings.extension")
    }

    private func openSettings(_ s: String) {
        if let url = URL(string: s) { NSWorkspace.shared.open(url) }
    }

    /// 屏幕录制权限要重新打开应用才生效
    func relaunch() {
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.createsNewApplicationInstance = true
        cfg.arguments = Array(CommandLine.arguments.dropFirst())
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: cfg) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }

    static let homepage = URL(string: "https://github.com/carey314/wechat-article-links")!
    func openHomepage() { NSWorkspace.shared.open(Self.homepage) }

    func openWeChat() {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: Self.wechatBundleID) else {
            flash("这台电脑上没找到微信")
            return
        }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }

    // MARK: 采过的公众号

    func reloadAccounts() {
        let files = (try? FileManager.default.contentsOfDirectory(at: dataDir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        let list = files.filter { $0.pathExtension == "md" && !$0.lastPathComponent.hasPrefix(".") }.map { url -> Account in
            let file = url.lastPathComponent
            let date = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            let count: Int
            if let c = countCache[file], c.0 == date {
                count = c.1
            } else {
                count = ((try? String(contentsOf: url, encoding: .utf8)) ?? "").split(separator: "\n").compactMap(extractURL).count
                countCache[file] = (date, count)
            }
            return Account(file: file, name: url.deletingPathExtension().lastPathComponent, count: count, modified: date)
        }.sorted { $0.modified > $1.modified }
        if list != accounts { accounts = list }
    }

    /// md 里的链接按顺序列出来，标题从进度记录里找
    func articles(of file: String) -> [Article] {
        let md = url(of: file)
        let date = (try? md.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
        if let c = articleCache[file], c.0 == date { return c.1 }
        let urls = ((try? String(contentsOf: md, encoding: .utf8)) ?? "").split(separator: "\n").compactMap(extractURL)
        var titles: [String: String] = [:]
        let state = dataDir.appendingPathComponent(file + ".已点过.txt")
        for line in ((try? String(contentsOf: state, encoding: .utf8)) ?? "").split(separator: "\n") {
            let f = line.split(separator: "\t", maxSplits: 2, omittingEmptySubsequences: false)
            if f.count > 2, !f[1].isEmpty { titles[String(f[1])] = String(f[2]) }
        }
        let list = urls.enumerated().map { Article(id: $0.offset + 1, title: titles[$0.element] ?? "", url: $0.element) }
        articleCache[file] = (date, list)
        return list
    }

    func url(of file: String) -> URL { dataDir.appendingPathComponent(file) }
    func openFile(_ file: String) { NSWorkspace.shared.open(url(of: file)) }
    func reveal(_ file: String) { NSWorkspace.shared.activateFileViewerSelecting([url(of: file)]) }
    func openDataFolder() { NSWorkspace.shared.open(dataDir) }
    func openLink(_ s: String) { if let u = URL(string: s) { NSWorkspace.shared.open(u) } }

    func copy(_ s: String, note: String = "已复制链接") {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
        flash(note)
    }

    func copyAll(_ file: String) {
        let list = articles(of: file)
        copy(list.map(\.url).joined(separator: "\n"), note: "已复制 \(list.count) 条链接")
    }

    func flash(_ s: String) {
        withAnimation(.snappy) { toast = s }
        toastTask?.cancel()
        toastTask = Task {
            try? await Task.sleep(for: .seconds(1.6))
            if !Task.isCancelled { withAnimation(.snappy) { self.toast = nil } }
        }
    }

    func chooseDataDir() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "用这个文件夹"
        panel.message = "选一个文件夹，存放采到的链接"
        panel.directoryURL = dataDir
        guard panel.runModal() == .OK, let url = panel.url else { return }
        dataDir = url
        UserDefaults.standard.set(url.path, forKey: Self.dataDirKey)
        countCache = [:]
        articleCache = [:]
        selection = .home
        lastRun = nil
        logs = []
        reloadAccounts()
        loadPreviousResult()
    }

    /// 打开应用时，把上次运行的结果显示出来
    private func loadPreviousResult() {
        guard let d = try? Data(contentsOf: dataDir.appendingPathComponent("status.json")),
              let obj = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return }
        let s = EngineStatus(obj)
        if !s.isActive && !s.state.isEmpty { lastRun = s }
        // 上一次运行的记录：日志文件里最后一次「等你松开鼠标键盘」之后的那些行
        let text = (try? String(contentsOf: dataDir.appendingPathComponent("运行日志.txt"), encoding: .utf8)) ?? ""
        let lines = text.split(separator: "\n").map(String.init)
        guard lastRun != nil, let start = lines.lastIndex(where: { $0.contains("等你松开鼠标键盘") }) else { return }
        logs = lines[start...].suffix(400).enumerated().map { parseLogLine($0.element, id: $0.offset + 1) }
        logsFromLastRun = true
    }

    // MARK: 运行引擎

    func start() {
        guard canStart else { return }
        runID = UUID().uuidString
        var args = ["--dir", dataDir.path, "--run-id", runID]
        switch mode {
        case .all: break
        case .trial: args += ["--max", "10"]
        case .recheck: args += ["--recheck"]
        }
        let p = Process()
        p.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/wxlinks-engine")
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        pipe.fileHandleForReading.readabilityHandler = { h in
            let d = h.availableData
            guard !d.isEmpty else { return }
            let s = String(decoding: d, as: UTF8.self)
            DispatchQueue.main.async { MainActor.assumeIsolated { AppModel.shared.consume(s) } }
        }
        p.terminationHandler = { _ in
            DispatchQueue.main.async { MainActor.assumeIsolated { AppModel.shared.engineExited() } }
        }
        logs = []
        logsFromLastRun = false
        pending = ""
        lastRun = nil
        stopping = false
        status = EngineStatus()
        status.state = "starting"
        status.runID = runID
        do {
            try p.run()
        } catch {
            status.state = "error"
            status.reason = "启动失败：\(error.localizedDescription)"
            lastRun = status
            return
        }
        process = p
        running = true
        startedAt = Date()
        menuBarShown = true
        // 采一次要很久，别让屏幕睡着：屏幕一黑就看不清微信了
        activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleDisplaySleepDisabled], reason: "采集公众号链接")
        statusTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
            MainActor.assumeIsolated { AppModel.shared.readStatus() }
        }
    }

    func stop() {
        guard let p = process, p.isRunning else { return }
        stopping = true
        p.terminate()   // 引擎收到后在下一步停下，并把剪贴板还原
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) {
            if p.isRunning { kill(p.processIdentifier, SIGKILL) }
        }
    }

    /// 退出应用时用：停掉引擎，最多等几秒
    func stopAndWait() {
        guard let p = process, p.isRunning else { return }
        p.terminate()
        let deadline = Date().addingTimeInterval(4)
        while p.isRunning && Date() < deadline { usleep(100_000) }
        if p.isRunning { kill(p.processIdentifier, SIGKILL) }
    }

    func dismissResult() { withAnimation(.snappy) { lastRun = nil } }

    var stateLabel: String {
        if stopping { return "正在停止" }
        switch status.state {
        case "waiting_idle": return "准备开始"
        case "running": return "采集中"
        case "paused": return "已暂停"
        default: return "正在启动"
        }
    }

    /// 菜单栏里的一行状态
    var statusLine: String {
        let who = status.account.isEmpty ? "" : " · \(status.account)"
        return "\(stateLabel)\(who) · \(status.total) 条"
    }

    private func consume(_ s: String) {
        pending += s
        var lines = pending.components(separatedBy: "\n")
        pending = lines.removeLast()
        for l in lines where !l.trimmingCharacters(in: .whitespaces).isEmpty {
            logs.append(parseLogLine(l, id: (logs.last?.id ?? 0) + 1))
        }
        if logs.count > 800 { logs.removeFirst(logs.count - 800) }
    }

    private func readStatus() {
        guard let d = try? Data(contentsOf: dataDir.appendingPathComponent("status.json")),
              let obj = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return }
        let s = EngineStatus(obj)
        guard s.runID == runID, s != status else { return }
        if s.total != status.total {
            withAnimation(.snappy) { status = s }
            reloadAccounts()
        } else {
            status = s
        }
    }

    private func engineExited() {
        if let h = (process?.standardOutput as? Pipe)?.fileHandleForReading {
            h.readabilityHandler = nil
            consume(String(decoding: h.readDataToEndOfFile(), as: UTF8.self))
        }
        if !pending.isEmpty { consume("\n") }
        readStatus()
        statusTimer?.invalidate()
        statusTimer = nil
        if let a = activity { ProcessInfo.processInfo.endActivity(a) }
        activity = nil
        process = nil
        running = false
        stopping = false
        menuBarShown = false
        var result = status
        if result.runID != runID || result.isActive {
            // 引擎没来得及写结果就退了（比如已经有一个在跑）
            result.state = "error"
            if result.reason.isEmpty { result.reason = logs.last?.text ?? "程序意外退出" }
        }
        lastRun = result
        reloadAccounts()
        NSApp.activate(ignoringOtherApps: true)
        if NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
            for w in NSApp.windows where w.canBecomeMain { w.makeKeyAndOrderFront(nil) }
        } else {
            reopenWindow?()
        }
        NSSound(named: result.state == "finished" ? "Glass" : "Funk")?.play()
    }
}
