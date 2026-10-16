using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WXLinks.Engine;

/// 截下来的一块画面：亮度数组 + 原图（存诊断截图用）
public sealed class Shot(Bitmap bmp, byte[] luma, Box region) : IDisposable
{
    public Bitmap Bitmap { get; } = bmp;
    public byte[] Luma { get; } = luma;
    public Box Region { get; } = region;
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public byte At(int x, int y) => Luma[y * Width + x];
    public void Dispose() => Bitmap.Dispose();
}

/// 和 Windows 打交道的部分：找微信窗口、截屏、认字、鼠标键盘、剪贴板
public static class Platform
{
    // MARK: Win32

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

    const int SW_RESTORE = 9;
    const uint GA_ROOT = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int DWMWA_CLOAKED = 14;
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04, MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_KEYUP = 0x02;
    const uint WM_CLOSE = 0x0010;
    public const ushort VK_ESCAPE = 0x1B, VK_HOME = 0x24, VK_MENU = 0x12, VK_CONTROL = 0x11, VK_W = 0x57;

    public static double DpiScale(IntPtr h) { var d = GetDpiForWindow(h); return d == 0 ? 1 : d / 96.0; }
    public static bool Exists(IntPtr h) => IsWindow(h);
    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    public static IntPtr Foreground() => GetForegroundWindow();
    public static uint PidOf(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return pid; }

    /// Ctrl+某个键
    public static void Chord(ushort vk)
    {
        var size = Marshal.SizeOf<INPUT>();
        INPUT K(ushort k, bool up) => new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = k, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };
        SendInput(4, [K(VK_CONTROL, false), K(vk, false), K(vk, true), K(VK_CONTROL, true)], size);
    }

    public static void InitDpi() => SetProcessDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2

    // MARK: 微信窗口

    /// 微信 4.x 的进程叫 Weixin，3.x 叫 WeChat；文章、公众号主页在 WeChatAppEx 里打开
    static readonly string[] WeChatProcesses = ["Weixin", "WeChat", "WeChatAppEx"];

    public static HashSet<uint> WeChatPids() =>
        WeChatProcesses.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return []; } })
            .Select(p => (uint)p.Id).ToHashSet();

    public static bool WeChatRunning() => WeChatPids().Count > 0;

    public record WinInfo(IntPtr Handle, uint Pid, string ProcessName, string Title, string Class, Box Bounds, bool Minimized);

    /// 屏幕上能看见的顶层窗口，按前后顺序（最前面的在前）
    public static List<WinInfo> Windows()
    {
        var list = new List<WinInfo>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;   // 其他虚拟桌面上的
            GetWindowThreadProcessId(h, out var pid);
            if (!names.TryGetValue(pid, out var pname))
            {
                try { pname = Process.GetProcessById((int)pid).ProcessName; } catch { pname = ""; }
                names[pid] = pname;
            }
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            list.Add(new WinInfo(h, pid, pname, t.ToString(), c.ToString(), Bounds(h), IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// 窗口在屏幕上的位置（不含 Windows 10/11 窗口外面看不见的阴影边）
    public static Box Bounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out r);
        return new Box(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static bool IsWeChat(WinInfo w) => WeChatProcesses.Any(n => n.Equals(w.ProcessName, StringComparison.OrdinalIgnoreCase));

    /// 公众号主页所在的窗口：最前面那个够大的微信窗口（Windows 版公众号常单独开一个窗口，也可能嵌在主窗口里）
    public static WinInfo? FindWeChatWindow() =>
        Windows().FirstOrDefault(w => IsWeChat(w) && !w.Minimized && w.Bounds.W > 500 && w.Bounds.H > 400);

    public static WinInfo? FindWeChatWindowAny() =>
        Windows().FirstOrDefault(w => IsWeChat(w) && w.Bounds.W > 500 && w.Bounds.H > 400);

    public static bool WeChatIsFront()
    {
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        return WeChatPids().Contains(pid);
    }

    /// 把公众号窗口拉到最前面（最小化了就还原）。Windows 不让后台程序随便抢焦点：直接切不过去时，先按一下 Alt 再切
    /// （Alt 在浏览器窗口里可能把焦点放到菜单上，所以能直接切就不按）
    public static void BringToFront(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        if (GetForegroundWindow() == h) return;
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (GetForegroundWindow() == h) return;
        keybd_event((byte)VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(h);
    }

    /// 某一点上最顶层的是不是这个窗口（判断有没有别的窗口挡着）
    public static bool TopmostAt(IntPtr h, int x, int y)
    {
        var at = WindowFromPoint(new POINT { X = x, Y = y });
        return GetAncestor(at, GA_ROOT) == h;
    }

    // MARK: 截屏

    /// 截屏幕上的一块（屏幕坐标），返回原图和亮度
    public static Shot Capture(Box screen, Box region)
    {
        int w = Math.Max(1, (int)screen.W), h = Math.Max(1, (int)screen.H);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen((int)screen.X, (int)screen.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return new Shot(bmp, Luma(bmp), region);
    }

    public static unsafe byte[] Luma(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outp = new byte[bmp.Width * bmp.Height];
        try
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte* p = row + x * 4;   // B G R A
                    outp[y * bmp.Width + x] = (byte)((p[0] + p[1] + p[2]) / 3);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return outp;
    }

    // MARK: 认字（Windows 自带 OCR，需要系统里装了中文语言的识别组件）

    static OcrEngine? ocr;
    public static string OcrLanguage = "";

    /// 准备中文识别；没装中文识别组件就返回 false
    public static bool InitOcr()
    {
        foreach (var tag in new[] { "zh-Hans-CN", "zh-CN", "zh-Hans" })
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang)) { ocr = OcrEngine.TryCreateFromLanguage(lang); OcrLanguage = tag; break; }
            }
            catch { }
        }
        return ocr != null;
    }

    public static IReadOnlyList<string> OcrLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// 认一张图里的字。小图先放大，系统 OCR 对小字认得不准
    public static List<TextBox> Recognize(Bitmap bmp, Box region)
    {
        if (ocr == null) return [];
        // 系统识别对小字认得不准：按屏幕缩放放大，100% 的屏幕放大 2 倍、200% 不放大
        double scale = Math.Max(1, Math.Min(3, 2.0 / Math.Max(Core.Scale, 0.5)));
        int max = (int)OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, Math.Min((double)max / bmp.Width, (double)max / bmp.Height));
        if (scale < 1) scale = 1;
        using var src = scale > 1.05 ? Resize(bmp, (int)(bmp.Width * scale), (int)(bmp.Height * scale)) : (Bitmap)bmp.Clone();
        double k = (double)src.Width / bmp.Width;

        using var ms = new MemoryStream();
        src.Save(ms, ImageFormat.Png);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(ms.ToArray());
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        writer.DetachStream();
        stream.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var sb = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        var result = ocr.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();

        var boxes = new List<TextBox>();
        foreach (var line in result.Lines)
        {
            // 系统 OCR 一行会拆成很多「词」，中文常常一个字一个词；按间距把挨着的拼回一段，和 Mac 版认出来的样子一致
            var words = line.Words.OrderBy(w => w.BoundingRect.X).ToList();
            var seg = new List<OcrWord>();
            void Flush()
            {
                if (seg.Count == 0) return;
                double x0 = seg.Min(w => w.BoundingRect.X), y0 = seg.Min(w => w.BoundingRect.Y);
                double x1 = seg.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = seg.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                var text = new StringBuilder();
                for (int i = 0; i < seg.Count; i++)
                {
                    if (i > 0 && NeedsSpace(seg[i - 1].Text, seg[i].Text)) text.Append(' ');
                    text.Append(seg[i].Text);
                }
                boxes.Add(new TextBox(text.ToString(), new Box(region.X + x0 / k, region.Y + y0 / k, (x1 - x0) / k, (y1 - y0) / k)));
                seg.Clear();
            }
            foreach (var w in words)
            {
                if (seg.Count > 0)
                {
                    var prev = seg[^1].BoundingRect;
                    double gap = w.BoundingRect.X - (prev.X + prev.Width);
                    double charH = Math.Max(prev.Height, w.BoundingRect.Height);
                    if (gap > charH * 1.6) Flush();   // 隔得远：是另一段（比如「全部」和「文章」分栏）
                }
                seg.Add(w);
            }
            Flush();
        }
        return boxes;
    }

    static Bitmap Resize(Bitmap b, int w, int h)
    {
        var r = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(r);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(b, 0, 0, w, h);
        return r;
    }

    /// 两个英文/数字词之间要补空格，中文之间不要
    static bool NeedsSpace(string a, string b) =>
        a.Length > 0 && b.Length > 0 && char.IsAsciiLetterOrDigit(a[^1]) && char.IsAsciiLetterOrDigit(b[0]);

    // MARK: 鼠标键盘

    public static POINT Cursor() { GetCursorPos(out var p); return p; }

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void LeftClick()
    {
        var down = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } };
        var up = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    /// 滚轮：delta 为正往上滚，为负往下滚（120 是一格）
    public static void Wheel(int delta)
    {
        var e = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = delta } } };
        SendInput(1, [e], Marshal.SizeOf<INPUT>());
    }

    public static void Key(ushort vk)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } };
        var up = new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    /// 键盘鼠标多久没动了
    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked(GetTickCount() - info.dwTime) / 1000.0;
    }

    // MARK: 剪贴板（要在 STA 线程里用）

    public static uint ClipboardSeq() => GetClipboardSequenceNumber();

    public static string? ClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; }
            catch { Thread.Sleep(100); }   // 别的程序正占着剪贴板
        }
        return null;
    }

    public static void SetClipboardText(string s)
    {
        for (int i = 0; i < 5; i++)
        {
            try { System.Windows.Forms.Clipboard.SetText(s); return; } catch { Thread.Sleep(100); }
        }
    }
}
