using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
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
        if (IsInstalledCopy) { try { if (!File.Exists(StartMenuShortcut)) MakeShortcut(StartMenuShortcut); } catch (Exception e) { Note(e); } return false; }
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
                        // 旧版本可能只剩后台的采集进程（界面被强制关掉时会留下，没有窗口，你找不到它），所以由这里替你关
                        var old = RunningOld();
                        var r = MessageBox.Show(old.Count > 0
                                ? "「公众号链接采集」的旧版本还开着（也可能是在后台没退出的采集），新版本装不进去。\n\n点「确定」把旧版本关掉、装上新版本。\n点「取消」就先直接用这一份。"
                                : "「公众号链接采集」的旧版本正开着，新版本装不进去。\n\n请先关掉它（右下角托盘里有图标的话也要退出），再点「确定」。\n点「取消」就先直接用这一份。",
                            "公众号链接采集", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                        if (r != MessageBoxResult.OK) return false;
                        foreach (var p in old) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                    }
                }
            }
            // 去掉「从网上下载的」标记（等于在文件属性里点「解除锁定」）：你已经同意运行过它，从开始菜单再打开就不会每次都拦
            try { File.Delete(Exe + ":Zone.Identifier"); } catch { }
            MakeShortcut(StartMenuShortcut);
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Dir });
            return true;
        }
        catch (Exception e)
        {
            Note(e);
            return false;   // 装不上（比如没有写权限）就直接用这一份
        }
    }

    /// 正在跑的装好的那份（界面或后台采集）
    static List<Process> RunningOld() =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)).Where(p =>
        {
            try { return p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToList();

    public static string NoteFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WXLinks", "安装记录.txt");

    /// 装的时候出了错，记一笔（遇到问题时和崩溃记录一起发给作者）
    static void Note(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NoteFile)!);
            File.AppendAllText(NoteFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Environment.ProcessPath} → {Exe}\n{e}\n\n");
        }
        catch { }
    }

    static bool SameFile(string a, string b)
    {
        if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// 建快捷方式，指向装好的那份（没装上就指向正在运行的这一份）。直接用系统的 IShellLink，不经过脚本组件
    public static void MakeShortcut(string lnk)
    {
        var target = File.Exists(Exe) ? Exe : Environment.ProcessPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            link.SetDescription("把公众号的文章链接按顺序收进一个文件");
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
