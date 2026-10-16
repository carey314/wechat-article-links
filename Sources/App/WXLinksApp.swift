import SwiftUI

@main
struct WXLinksApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        Window("公众号链接采集", id: "main") {
            ContentView().environmentObject(model)
        }
        .defaultSize(width: 960, height: 640)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) {}
            CommandMenu("采集") {
                Button(model.running ? "停止采集" : "开始采集") {
                    if model.running { model.stop() } else { model.start() }
                }
                .keyboardShortcut("r", modifiers: .command)
                .disabled(!model.running && !model.canStart)
                Divider()
                Button("打开数据文件夹") { model.openDataFolder() }
                Button("更改数据文件夹…") { model.chooseDataDir() }.disabled(model.running)
            }
        }

        Settings {
            SettingsView().environmentObject(model)
        }

        // 采集时微信盖在主窗口上面，屏幕顶上的菜单栏显示已采条数，点开能停止
        MenuBarExtra(isInserted: model.binding(\.menuBarShown)) {
            MenuBarContent().environmentObject(model)
        } label: {
            Text("\(Image(systemName: "link")) \(model.status.total)")
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.activate(ignoringOtherApps: true)
        // 调试用：--appearance dark / light 强制界面外观
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--appearance"), i + 1 < args.count {
            NSApp.appearance = NSAppearance(named: args[i + 1] == "dark" ? .darkAqua : .aqua)
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        AppModel.shared.stopAndWait()
        return .terminateNow
    }
}
