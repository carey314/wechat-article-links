import SwiftUI

// MARK: - 通用小部件

/// 绿底白字的主按钮。系统自带的实心按钮在窗口失去焦点时底色变浅灰、文字还是白的，就看不见了，所以自己画
struct PrimaryButtonStyle: ButtonStyle {
    var large = false
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(large ? .body.weight(.semibold) : .callout.weight(.semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, large ? 18 : 12)
            .frame(height: large ? 34 : 26)
            .background(
                Capsule().fill(LinearGradient(colors: [brandGreenLight, brandGreen], startPoint: .top, endPoint: .bottom))
            )
            .overlay(Capsule().strokeBorder(.black.opacity(0.08), lineWidth: 0.5))
            .shadow(color: brandGreen.opacity(isEnabled ? 0.3 : 0), radius: 2, y: 1)
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.4)
            .contentShape(Capsule())
    }
}

struct Card<Content: View>: View {
    var padding: CGFloat = 20
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color(nsColor: .separatorColor).opacity(0.5), lineWidth: 0.5))
            .shadow(color: .black.opacity(0.05), radius: 2, y: 1)
    }
}

struct AppTile: View {
    var size: CGFloat = 50
    var symbol = "link"

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
            .fill(LinearGradient(colors: [brandGreenLight, brandGreen], startPoint: .top, endPoint: .bottom))
            .frame(width: size, height: size)
            .overlay(Image(systemName: symbol).font(.system(size: size * 0.46, weight: .semibold)).foregroundStyle(.white))
            .shadow(color: brandGreen.opacity(0.28), radius: size * 0.12, y: size * 0.06)
    }
}

struct StatePill: View {
    let text: String
    let color: Color
    var pulsing = false
    @State private var dim = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 7) {
            Circle().fill(color).frame(width: 7, height: 7).opacity(dim ? 0.3 : 1)
            Text(text).font(.caption.weight(.semibold))
        }
        .foregroundStyle(color)
        .padding(.horizontal, 11)
        .frame(height: 24)
        .background(color.opacity(0.12), in: Capsule())
        .onAppear {
            guard pulsing, !reduceMotion else { return }
            withAnimation(.easeInOut(duration: 0.8).repeatForever()) { dim = true }
        }
    }
}

struct Stat: View {
    let label: String
    let value: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.body.weight(.semibold)).monospacedDigit()
        }
    }
}

struct Notice: View {
    let symbol: String
    let text: String
    var tint: Color = .orange
    var actionTitle: String?
    var action: (() -> Void)?

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol).foregroundStyle(tint)
            Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            if let actionTitle, let action { Button(actionTitle, action: action) }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .background(tint.opacity(0.1), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

var screenPermissionName: String {
    if #available(macOS 15, *) { return "录屏与系统录音" }
    return "屏幕录制"
}

// MARK: - 主窗口

struct ContentView: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        NavigationSplitView {
            Sidebar()
                .navigationSplitViewColumnWidth(min: 200, ideal: 224, max: 320)
        } detail: {
            switch model.selection ?? .home {
            case .home: HomeView()
            case .setup: SetupView()
            case .account(let file): AccountView(file: file).id(file)
            }
        }
        .frame(minWidth: 780, minHeight: 540)
        .tint(brandGreen)
        .overlay(alignment: .bottom) {
            if let t = model.toast {
                Text(t)
                    .font(.callout.weight(.medium))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 8)
                    .background(.regularMaterial, in: Capsule())
                    .shadow(color: .black.opacity(0.12), radius: 8, y: 3)
                    .padding(.bottom, 22)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .onAppear { model.reopenWindow = { openWindow(id: "main") } }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            model.refreshEnvironment()
            model.reloadAccounts()
        }
    }
}

struct Sidebar: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        List(selection: model.binding(\.selection)) {
            Label("采集", systemImage: "link")
                .badge(model.running ? Text("采集中") : nil)
                .tag(SidebarItem.home)
            Label("设置与权限", systemImage: "gearshape")
                .badge(model.permissionsOK ? nil : Text("缺 \(model.missingPermissions) 项"))
                .tag(SidebarItem.setup)
            Section("公众号") {
                if model.accounts.isEmpty {
                    Text("采完的公众号会排在这里")
                        .font(.callout)
                        .foregroundStyle(.tertiary)
                        .selectionDisabled()
                }
                ForEach(model.accounts) { a in
                    Label(a.name, systemImage: "book.closed")
                        .badge(a.count)
                        .tag(SidebarItem.account(a.file))
                }
            }
        }
        .listStyle(.sidebar)
        .safeAreaInset(edge: .bottom) {
            Button { model.openDataFolder() } label: {
                Label("数据文件夹", systemImage: "folder")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 18)
            .padding(.vertical, 12)
            .help(model.dataDir.path)
        }
    }
}

// MARK: - 采集页

struct HomeView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Group {
            if model.running {
                VStack(spacing: 14) {
                    RunningCard()
                    LogCard(title: "运行记录").frame(maxHeight: .infinity)
                }
                .frame(maxWidth: 860)
                .padding(20)
            } else {
                ScrollView {
                    VStack(spacing: 14) {
                        if !model.permissionsOK { PermissionCard() }
                        ReadyCard()
                        if let r = model.lastRun { ResultCard(result: r) }
                        if !model.logs.isEmpty { LogCard(title: "这次的运行记录").frame(height: 300) }
                    }
                    .frame(maxWidth: 860)
                    .padding(20)
                    .frame(maxWidth: .infinity)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle("采集")
        .navigationSubtitle(model.running ? "正在采集" : "就绪")
    }
}

struct PermissionCard: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Card(padding: 24) {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 14) {
                    Image(systemName: "lock.shield")
                        .font(.system(size: 28, weight: .medium))
                        .foregroundStyle(brandGreen)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("第一次使用，先打开两个权限").font(.title3.weight(.semibold))
                        Text("替你点微信、看清屏幕上的标题，macOS 都要你先同意。只对这个应用生效，随时可以在系统设置里关掉。")
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                PermissionRow(title: "辅助功能", detail: "用来替你点击微信里的文章和按钮", ok: model.axOK) { model.requestAccessibility() }
                PermissionRow(title: screenPermissionName, detail: "用来看清微信窗口里的标题和按钮位置", ok: model.screenOK) { model.requestScreenRecording() }
                HStack(alignment: .firstTextBaseline) {
                    Text("在系统设置里打开「公众号链接采集」，再回到这里。\(screenPermissionName)打开后，要重新打开本应用才生效。")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 12)
                    if !model.screenOK && (model.screenRequested || model.axOK) {
                        Button("重新打开应用") { model.relaunch() }
                    }
                    Button("设置与权限") { model.selection = .setup }
                }
            }
        }
    }
}

struct PermissionRow: View {
    let title: String
    let detail: String
    let ok: Bool
    let action: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle")
                .font(.title3)
                .foregroundStyle(ok ? brandGreen : Color.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).fontWeight(.medium)
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            if ok {
                Text("已打开").font(.callout).foregroundStyle(brandGreen)
            } else {
                Button("去打开", action: action)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

struct StepChip: View {
    let n: Int
    let text: String

    var body: some View {
        HStack(spacing: 8) {
            Text("\(n)")
                .font(.caption.weight(.bold))
                .foregroundStyle(brandGreen)
                .frame(width: 20, height: 20)
                .background(brandGreen.opacity(0.15), in: Circle())
            Text(text).font(.callout)
        }
        .padding(.vertical, 5)
        .padding(.leading, 5)
        .padding(.trailing, 13)
        .background(Color.primary.opacity(0.05), in: Capsule())
    }
}

struct ReadyCard: View {
    @EnvironmentObject var model: AppModel

    @ViewBuilder private var steps: some View {
        StepChip(n: 1, text: "微信里打开公众号主页")
        StepChip(n: 2, text: "点「开始采集」")
        StepChip(n: 3, text: "松开鼠标，等它采完")
    }

    var body: some View {
        Card(padding: 24) {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 16) {
                    AppTile(size: 50)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("把公众号的文章链接，按顺序收进一个文件").font(.title3.weight(.semibold))
                        Text("采完按从新到旧排好，一行一条，方便依次学习。").foregroundStyle(.secondary)
                    }
                }
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 8) { steps }
                    VStack(alignment: .leading, spacing: 6) { steps }
                }
                Divider()
                HStack(spacing: 12) {
                    Picker("范围", selection: model.binding(\.mode)) {
                        ForEach(RunMode.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .fixedSize()
                    Spacer()
                    Button { model.start() } label: {
                        Label("开始采集", systemImage: "play.fill").padding(.horizontal, 6)
                    }
                    .buttonStyle(PrimaryButtonStyle(large: true))
                    .disabled(!model.canStart)
                    .keyboardShortcut(.defaultAction)
                }
                Text(model.mode.hint).font(.callout).foregroundStyle(.secondary)
                HStack(spacing: 16) {
                    ConditionChip(ok: model.axOK, text: "辅助功能")
                    ConditionChip(ok: model.screenOK, text: screenPermissionName)
                    ConditionChip(ok: model.wechatRunning, text: model.wechatRunning ? "微信已打开" : "微信没打开")
                    ConditionChip(ok: !model.darkMode, text: model.darkMode ? "深色模式" : "浅色模式")
                    Spacer()
                    Button { model.selection = .setup } label: {
                        Label("设置与权限", systemImage: "chevron.right").labelStyle(TrailingIconLabelStyle())
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(brandGreen)
                    .font(.callout)
                }
                if model.permissionsOK && !model.wechatRunning {
                    Notice(symbol: "exclamationmark.triangle.fill", text: "微信没有打开。先打开微信，进入要采的公众号主页。",
                           actionTitle: "打开微信") { model.openWeChat() }
                }
                if model.darkMode {
                    Notice(symbol: "moon.fill", text: "电脑现在是深色模式，微信会跟着变深，程序认不出来。采集前请先切到浅色。",
                           actionTitle: "外观设置") { model.openAppearanceSettings() }
                }
            }
        }
    }
}

struct RunningCard: View {
    @EnvironmentObject var model: AppModel

    private var pill: StatePill {
        if model.stopping { return StatePill(text: "正在停止", color: .secondary) }
        switch model.status.state {
        case "running": return StatePill(text: "采集中", color: brandGreen, pulsing: true)
        case "paused": return StatePill(text: "已暂停", color: .orange)
        case "waiting_idle": return StatePill(text: "准备开始", color: .blue, pulsing: true)
        default: return StatePill(text: "正在启动", color: .secondary)
        }
    }

    private var hint: String {
        switch model.status.state {
        case "waiting_idle": "请松开鼠标和键盘，5 秒后开始替你操作微信。"
        case "paused": "你碰了鼠标或切走了微信，已暂停。停手 5 秒后自动继续。"
        default: "正在替你操作微信，请别碰鼠标。鼠标甩到屏幕左上角立即停止。"
        }
    }

    private var subtitle: String {
        let s = model.status
        if s.account.isEmpty { return "正在找公众号主页…" }
        return "\(s.account) · " + (s.phase == "recheck" ? "补漏中（从头再扫一遍）" : "第一遍")
    }

    private var currentLine: String {
        model.status.lastTitle.isEmpty ? (model.status.message.isEmpty ? "正在准备…" : model.status.message) : "刚采到：\(model.status.lastTitle)"
    }

    var body: some View {
        let s = model.status
        Card(padding: 24) {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    pill.id(model.stopping ? "stopping" : s.state)
                    Spacer()
                    if let t = model.startedAt {
                        TimelineView(.periodic(from: t, by: 1)) { ctx in
                            Text("已用 " + elapsed(ctx.date.timeIntervalSince(t)))
                                .font(.callout)
                                .monospacedDigit()
                                .foregroundStyle(.secondary)
                        }
                    }
                }
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("\(s.total)")
                        .font(.system(size: 58, weight: .semibold, design: .rounded))
                        .monospacedDigit()
                        .contentTransition(.numericText())
                    Text("条链接").font(.title3).foregroundStyle(.secondary)
                }
                Text(subtitle).foregroundStyle(.secondary)
                HStack(spacing: 30) {
                    Stat(label: "本次新增", value: "\(s.added)")
                    Stat(label: "平均每篇", value: s.avg > 0 ? String(format: "%.1f 秒", s.avg) : "—")
                    Stat(label: "补回", value: "\(s.recovered)")
                    if s.unreadable > 0 { Stat(label: "认不出标题", value: "\(s.unreadable)") }
                }
                Label(currentLine, systemImage: "doc.text")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Divider()
                HStack(spacing: 12) {
                    Text(hint).font(.callout).foregroundStyle(.secondary)
                    Spacer(minLength: 12)
                    Button { model.stop() } label: {
                        Label("停止", systemImage: "stop.fill").padding(.horizontal, 4)
                    }
                    .foregroundStyle(.red)
                    .buttonBorderShape(.capsule)
                    .controlSize(.large)
                    .disabled(model.stopping)
                    .keyboardShortcut(".", modifiers: .command)
                }
            }
        }
    }
}

func elapsed(_ t: TimeInterval) -> String {
    let s = max(0, Int(t))
    return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%02d:%02d", s / 60, s % 60)
}

struct LogCard: View {
    let title: String
    @EnvironmentObject var model: AppModel

    var body: some View {
        Card(padding: 0) {
            VStack(spacing: 0) {
                HStack {
                    Text(title).font(.headline)
                    Spacer()
                    Text("\(model.logs.count) 条").font(.caption).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 11)
                Divider()
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 0) {
                            ForEach(model.logs) { LogRow(line: $0).id($0.id) }
                        }
                        .padding(.vertical, 4)
                    }
                    .onChange(of: model.logs.last?.id) { _, id in
                        if let id { withAnimation(.snappy) { proxy.scrollTo(id, anchor: .bottom) } }
                    }
                    .onAppear {
                        if let id = model.logs.last?.id { proxy.scrollTo(id, anchor: .bottom) }
                    }
                }
            }
        }
    }
}

struct LogRow: View {
    let line: LogLine

    private var symbol: (String, Color) {
        switch line.kind {
        case .collected: ("checkmark.circle.fill", brandGreen)
        case .recovered: ("arrow.uturn.backward.circle.fill", .teal)
        case .warning: ("exclamationmark.triangle.fill", .orange)
        case .paused: ("pause.circle.fill", .orange)
        case .resumed: ("play.circle.fill", brandGreen)
        case .info: ("info.circle", .secondary)
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol.0).foregroundStyle(symbol.1).frame(width: 16)
            Text(line.text)
                .lineLimit(2)
                .foregroundStyle(line.kind == .info ? .secondary : .primary)
            Spacer(minLength: 8)
            if !line.detail.isEmpty {
                Text(line.detail).monospacedDigit().foregroundStyle(.secondary)
            }
            Text(line.time)
                .font(.system(size: 10.5, design: .monospaced))
                .foregroundStyle(.tertiary)
        }
        .font(.callout)
        .padding(.horizontal, 16)
        .padding(.vertical, 6)
    }
}

struct ResultCard: View {
    let result: EngineStatus
    @EnvironmentObject var model: AppModel

    private var look: (symbol: String, color: Color, title: String) {
        switch result.state {
        case "finished": ("checkmark.circle.fill", brandGreen, result.added > 0 ? "采完了" : "没有新文章")
        case "stopped": ("stop.circle.fill", .secondary, "已停止")
        case "needs_permission": ("lock.circle.fill", .orange, "还差权限")
        default: ("exclamationmark.triangle.fill", .orange, "没能完成")
        }
    }

    private var detail: String {
        let r = result
        let who = r.account.isEmpty ? "" : "\(r.account) "
        switch r.state {
        case "finished":
            var s = "\(who)共 \(r.total) 条，这次新增 \(r.added) 条"
            if r.recovered > 0 { s += "，补回 \(r.recovered) 条" }
            s += "。"
            if r.unreadable > 0 { s += "有 \(r.unreadable) 张卡片认不出标题，可以看看运行记录里的提醒。" }
            return s
        case "stopped":
            return "\(who)已采 \(r.total) 条。再点开始会从停下的地方接着采。"
        default:
            return r.reason.isEmpty ? r.message : r.reason
        }
    }

    var body: some View {
        Card(padding: 18) {
            HStack(spacing: 14) {
                Image(systemName: look.symbol).font(.system(size: 26)).foregroundStyle(look.color)
                VStack(alignment: .leading, spacing: 3) {
                    Text(look.title).font(.headline)
                    Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 12)
                if let file = result.file, model.accounts.contains(where: { $0.file == file }) {
                    Button("查看列表") { model.selection = .account(file) }
                    Button("打开文件") { model.openFile(file) }
                }
                Button { model.dismissResult() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("关掉这条结果")
            }
        }
    }
}

// MARK: - 公众号的链接列表

struct AccountView: View {
    let file: String
    @EnvironmentObject var model: AppModel
    @State private var query = ""
    @State private var selection = Set<Article.ID>()

    private func summary(_ account: Account?, _ n: Int) -> String {
        var s = "\(n) 篇 · 从新到旧"
        if let a = account {
            let f = RelativeDateTimeFormatter()
            f.locale = Locale(identifier: "zh_CN")
            s += " · \(f.localizedString(for: a.modified, relativeTo: Date()))更新"
        }
        return s
    }

    var body: some View {
        let account = model.accounts.first { $0.file == file }
        let all = model.articles(of: file)
        let q = query.trimmingCharacters(in: .whitespaces)
        let shown = q.isEmpty ? all : all.filter { $0.title.localizedCaseInsensitiveContains(q) || $0.url.localizedCaseInsensitiveContains(q) }
        let pick: (Set<Article.ID>) -> [Article] = { ids in all.filter { ids.contains($0.id) } }

        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 14) {
                AppTile(size: 40, symbol: "book.closed")
                VStack(alignment: .leading, spacing: 2) {
                    Text(account?.name ?? file).font(.title3.weight(.semibold))
                    Text(summary(account, all.count)).font(.callout).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 24)
            .padding(.top, 18)

            List(selection: $selection) {
                ForEach(shown) { ArticleRow(article: $0) }
            }
            .listStyle(.inset)
            .alternatingRowBackgrounds()
            .contextMenu(forSelectionType: Article.ID.self) { ids in
                let items = pick(ids)
                if !items.isEmpty {
                    Button(items.count == 1 ? "复制链接" : "复制 \(items.count) 条链接") {
                        model.copy(items.map(\.url).joined(separator: "\n"), note: "已复制 \(items.count) 条链接")
                    }
                    Button("复制为 Markdown") {
                        model.copy(items.map { "[\($0.title.isEmpty ? $0.url : $0.title)](\($0.url))" }.joined(separator: "\n"), note: "已复制")
                    }
                    Divider()
                    Button(items.count == 1 ? "在浏览器打开" : "在浏览器打开 \(items.count) 篇") {
                        items.prefix(20).forEach { model.openLink($0.url) }
                    }
                }
            } primaryAction: { ids in
                pick(ids).prefix(20).forEach { model.openLink($0.url) }
            }
            .onCopyCommand {
                let items = pick(selection)
                return items.isEmpty ? [] : [NSItemProvider(object: items.map(\.url).joined(separator: "\n") as NSString)]
            }
            .overlay {
                if shown.isEmpty {
                    if q.isEmpty {
                        ContentUnavailableView("还没有链接", systemImage: "link", description: Text("采到的链接会按顺序出现在这里。"))
                    } else {
                        ContentUnavailableView.search(text: q)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color(nsColor: .separatorColor).opacity(0.5), lineWidth: 0.5))
            .padding(.horizontal, 20)
            .padding(.bottom, 20)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle(account?.name ?? "")
        .navigationSubtitle("\(all.count) 篇")
        .searchable(text: $query, placement: .toolbar, prompt: "搜索标题")
        .toolbar {
            ToolbarItemGroup {
                Button { model.copyAll(file) } label: { Label("复制全部链接", systemImage: "doc.on.doc") }
                    .help("复制全部链接，一行一条")
                Button { model.openFile(file) } label: { Label("打开文件", systemImage: "doc.text") }
                    .help("用默认的编辑器打开这个 md 文件")
                Button { model.reveal(file) } label: { Label("在访达中显示", systemImage: "folder") }
                    .help("在访达中显示")
            }
        }
    }
}

struct ArticleRow: View {
    let article: Article
    @EnvironmentObject var model: AppModel
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            Text("\(article.id)")
                .font(.callout)
                .monospacedDigit()
                .foregroundStyle(.tertiary)
                .frame(width: 34, alignment: .trailing)
            VStack(alignment: .leading, spacing: 2) {
                Text(article.title.isEmpty ? "（没记下标题）" : article.title)
                    .lineLimit(1)
                    .foregroundStyle(article.title.isEmpty ? .secondary : .primary)
                Text(article.shortURL)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            if hovering {
                Button { model.copy(article.url) } label: { Image(systemName: "doc.on.doc") }
                    .help("复制链接")
                Button { model.openLink(article.url) } label: { Image(systemName: "arrow.up.forward.square") }
                    .help("在浏览器打开")
            }
        }
        .buttonStyle(.borderless)
        .padding(.vertical, 3)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
    }
}

// MARK: - 设置、菜单栏

struct ConditionChip: View {
    let ok: Bool
    let text: String

    var body: some View {
        Label(text, systemImage: ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
            .font(.callout)
            .foregroundStyle(ok ? Color.secondary : Color.orange)
            .labelStyle(ConditionLabelStyle(ok: ok))
    }
}

struct ConditionLabelStyle: LabelStyle {
    let ok: Bool
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 5) {
            configuration.icon.foregroundStyle(ok ? brandGreen : .orange)
            configuration.title
        }
    }
}

struct TrailingIconLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 3) { configuration.title; configuration.icon.imageScale(.small) }
    }
}

struct SetupRow<Trailing: View>: View {
    let ok: Bool
    let title: String
    let detail: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.title3)
                .foregroundStyle(ok ? brandGreen : .orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).fontWeight(.medium)
                Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 12)
            trailing
        }
        .padding(.vertical, 4)
    }
}

struct SetupView: View {
    @EnvironmentObject var model: AppModel

    private func section<Content: View>(_ title: String, @ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline).padding(.leading, 4)
            Card(padding: 18) { VStack(alignment: .leading, spacing: 10) { content() } }
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                section("权限") {
                    SetupRow(ok: model.axOK, title: "辅助功能", detail: "用来替你点击微信里的文章和按钮") {
                        if model.axOK {
                            Button("系统设置") { model.openAccessibilitySettings() }
                        } else {
                            Button("去打开") { model.requestAccessibility() }.buttonStyle(PrimaryButtonStyle())
                        }
                    }
                    Divider()
                    SetupRow(ok: model.screenOK, title: screenPermissionName, detail: "用来看清微信窗口里的标题和按钮位置。打开后要重新打开本应用才生效") {
                        if model.screenOK {
                            Button("系统设置") { model.openScreenSettings() }
                        } else {
                            Button("去打开") { model.requestScreenRecording() }.buttonStyle(PrimaryButtonStyle())
                        }
                    }
                    Divider()
                    HStack(alignment: .top, spacing: 12) {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("在系统设置的列表里打开「公众号链接采集」，回到这里会自动检测。")
                            Text("列表里明明开着、这里还显示没打开（更新应用后常这样）：在列表里选中它，点「−」删掉，再回来点「去打开」重新添加。")
                        }
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 12)
                        VStack(alignment: .trailing, spacing: 8) {
                            Button("重新检测") {
                                model.refreshEnvironment()
                                model.flash(model.permissionsOK ? "两个权限都已打开" : "还差 \(model.missingPermissions) 个权限")
                            }
                            Button("重新打开应用") { model.relaunch() }
                        }
                    }
                }

                section("运行条件") {
                    SetupRow(ok: model.wechatRunning, title: "微信", detail: model.wechatRunning ? "已打开。采集前进入要采的公众号主页" : "没有打开") {
                        if !model.wechatRunning { Button("打开微信") { model.openWeChat() } }
                    }
                    Divider()
                    SetupRow(ok: !model.darkMode, title: "外观", detail: model.darkMode ? "现在是深色模式，微信会跟着变深，程序认不出来。采集前请切到浅色" : "浅色模式") {
                        if model.darkMode { Button("外观设置") { model.openAppearanceSettings() } }
                    }
                }

                section("数据文件夹") {
                    Text(model.dataDir.path)
                        .font(.system(.callout, design: .monospaced))
                        .textSelection(.enabled)
                        .lineLimit(2)
                        .truncationMode(.middle)
                    Text("每个公众号一个 md 文件。旁边的「.已点过.txt」记着采到哪儿了，删掉就会从头采。")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    HStack {
                        Button("更改…") { model.chooseDataDir() }.disabled(model.running)
                        Button("在访达中显示") { model.openDataFolder() }
                    }
                }

                section("安装") {
                    SetupRow(ok: model.installedInApplications, title: model.installedInApplications ? "已装在「应用程序」里" : "还没装进「应用程序」",
                             detail: model.installedInApplications
                                ? "版本 \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")"
                                : "现在从「\((Bundle.main.bundlePath as NSString).deletingLastPathComponent)」运行。放在下载文件夹或安装包里直接用，系统可能把它当成新应用，权限会对不上") {
                        if !model.installedInApplications {
                            Button("移到「应用程序」") { model.moveToApplications() }.buttonStyle(PrimaryButtonStyle())
                        }
                    }
                    Divider()
                    HStack {
                        Text("开源项目，MIT 协议。新版本、问题反馈都在项目主页。").font(.callout).foregroundStyle(.secondary)
                        Spacer()
                        Button("项目主页") { model.openHomepage() }
                    }
                }
            }
            .frame(maxWidth: 760, alignment: .leading)
            .padding(24)
            .frame(maxWidth: .infinity)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .navigationTitle("设置与权限")
        .navigationSubtitle(model.permissionsOK ? "一切就绪" : "还差 \(model.missingPermissions) 个权限")
    }
}

struct SettingsView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        SetupView()
            .frame(width: 640, height: 680)
            .tint(brandGreen)
    }
}

struct MenuBarContent: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(model.statusLine)
        if !model.status.lastTitle.isEmpty {
            Text("刚采到：" + String(model.status.lastTitle.prefix(26)))
        }
        Divider()
        Button("停止采集") { model.stop() }.disabled(model.stopping)
        Button("打开主窗口") {
            NSApp.activate(ignoringOtherApps: true)
            openWindow(id: "main")
        }
    }
}
