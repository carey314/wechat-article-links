using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;

namespace WXLinks;

/// 第一次打开下载的 exe：把自己复制到 %LOCALAPPDATA%\Programs\公众号链接采集\，在开始菜单建快捷方式，再从那里打开。
/// 以后下载新版本再打开一次，就覆盖旧的。带 --no-install 启动、或者在开发目录（bin 里）运行时不装
static class Installer
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "公众号链接采集");
    public static string Exe => Path.Combine(Dir, "WXLinks.exe");
    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "公众号链接采集.lnk");
    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "公众号链接采集.lnk");

    public static bool IsInstalledCopy =>
        Environment.ProcessPath is string p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    /// 返回 true：已经交给装好的那份打开了，这一份直接退出
    public static bool Run(string[] args)
    {
        var self = Environment.ProcessPath;
        if (self == null || args.Contains("--no-install")) return false;
        if (IsInstalledCopy) { try { if (!File.Exists(StartMenuShortcut)) MakeShortcut(StartMenuShortcut); } catch { } return false; }
        if (self.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            if (!SameFile(self, Exe))
            {
                // 装好的旧版本正开着时覆盖不了：请你先关掉它
                while (true)
                {
                    try { File.Copy(self, Exe, true); break; }
                    catch (IOException)
                    {
                        var r = MessageBox.Show("「公众号链接采集」的旧版本正开着，新版本装不进去。\n\n请先关掉它（右下角托盘里有图标的话也要退出），再点「确定」。\n点「取消」就先直接用这一份。",
                            "公众号链接采集", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                        if (r != MessageBoxResult.OK) return false;
                    }
                }
            }
            // 去掉「从网上下载的」标记（等于在文件属性里点「解除锁定」）：你已经同意运行过它，从开始菜单再打开就不会每次都拦
            try { File.Delete(Exe + ":Zone.Identifier"); } catch { }
            MakeShortcut(StartMenuShortcut);
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Dir });
            return true;
        }
        catch
        {
            return false;   // 装不上（比如没有写权限）就直接用这一份
        }
    }

    static bool SameFile(string a, string b)
    {
        if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// 建快捷方式，指向装好的那份（没装上就指向正在运行的这一份）
    public static void MakeShortcut(string lnk)
    {
        var target = File.Exists(Exe) ? Exe : Environment.ProcessPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.IconLocation = target + ",0";
        sc.Description = "把公众号的文章链接按顺序收进一个文件";
        sc.Save();
    }
}
