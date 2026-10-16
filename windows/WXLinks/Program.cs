using System.IO;
using System.Windows;

namespace WXLinks;

public static class Program
{
    /// 带 --engine 启动时是采集引擎（界面在后台启动它）；否则打开窗口界面
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--engine") return Engine.Collector.Run(args[1..]);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject);
        if (Installer.Run(args)) return 0;
        var app = new App();
        app.DispatcherUnhandledException += (_, e) => Crash(e.Exception);
        app.InitializeComponent();
        return app.Run();
    }

    public static string CrashLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WXLinks", "崩溃记录.txt");

    /// 界面出错退出前留一份记录，并告诉你记录在哪（遇到问题时发给作者）
    static void Crash(object ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            var ver = typeof(Program).Assembly.GetName().Version?.ToString(3);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 版本 {ver}，{Environment.OSVersion}\n{ex}\n\n");
            Console.Error.WriteLine(ex);
            MessageBox.Show($"程序出错了，需要关闭。\n\n出错记录存在：\n{CrashLog}\n\n麻烦把这个文件发给作者（项目主页的「问题反馈」）。\n\n{(ex as Exception)?.Message}",
                "公众号链接采集", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
