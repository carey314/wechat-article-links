namespace WXLinks;

public static class Program
{
    /// 带 --engine 启动时是采集引擎（界面在后台启动它）；否则打开窗口界面
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--engine") return Engine.Collector.Run(args[1..]);
        if (Installer.Run(args)) return 0;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
