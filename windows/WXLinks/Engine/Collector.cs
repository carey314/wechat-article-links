using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static WXLinks.Engine.Core;

namespace WXLinks.Engine;

class StopError(string reason, bool byUser = false) : Exception(reason) { public bool ByUser { get; } = byUser; }
class Interrupted(string reason) : Exception(reason) { }

/// 采集引擎（界面在后台用 --engine 启动它）。流程和 Mac 版（Sources/engine.swift）一样：
/// 认出公众号主页 → 一屏一屏往下扫，没点过的卡片点开 →「···」→「复制链接」→ 关掉 → 采到底后从头再扫一遍补漏。
/// Windows 微信 4.x 的界面（2026-10 录屏实测）：公众号主页和文章都开在同一个「微信」浏览器窗口的标签里，
/// 点卡片后文章在主页标签右边新开一个标签；当前标签右端有「···」和「×」；「···」菜单里有「复制链接」；
/// 点「×」关掉文章后回到主页标签，列表停在原位。标签刚开时显示「微信公众平台」，加载出来才换成文章标题
public static class Collector
{
    // MARK: 配置和状态

    static int maxCount = int.MaxValue;
    static string runId = "", dataDir = "";
    static string? mdName;
    static bool recheckOnly, probeMode;
    static string logPath = "", statusPath = "", pidPath = "", stopPath = "", diagDir = "";
    const int RestEvery = 40;
    const double LoadTimeout = 25;

    static string account = "";
    static int added, reopened, recovered;
    static readonly HashSet<string> unreadable = [];
    static readonly List<double> durations = [];
    static bool recovering;
    static Records rec = new();
    static readonly Dictionary<string, object?> status = [];
    static readonly Random rnd = new();
    static StreamWriter stdout = StreamWriter.Null;

    static IntPtr wx;                  // 公众号主页所在的窗口
    static Box win;                    // 它在屏幕上的位置（像素）
    static Box? lastProfileTab;        // 上一次确认过的公众号主页标签，用来认出右边多开着的文章
    static double pxPerNotch;          // 滚轮一格页面移动多少像素（开始时量一次）
    static Platform.POINT lastPos;

    static double BandH => S(80);      // 截标签栏截这么高（标签栏实际多高由 Vision 按颜色找）
    static double ContentTop => S(40);

    static string Now => DateTime.Now.ToString("HH:mm:ss");

    static void Log(string s)
    {
        var line = $"[{Now}] {s}";
        try { stdout.WriteLine(line); } catch { }
        try { File.AppendAllText(logPath, line + "\n", new UTF8Encoding(false)); } catch { }
        SaveStatus(new() { ["message"] = s });
    }

    static void SaveStatus(Dictionary<string, object?>? changes = null)
    {
        if (probeMode) return;
        if (changes != null) foreach (var kv in changes) status[kv.Key] = kv.Value;
        double avg = durations.Count == 0 ? 0 : durations.Average();
        status["updated_at"] = DateTime.UtcNow.ToString("o");
        status["account"] = account;
        status["md_path"] = rec.MdPath;
        status["total_links"] = rec.Urls.Count;
        status["added_this_run"] = added;
        status["recovered"] = recovered;
        status["reopened"] = reopened;
        status["unreadable"] = unreadable.Count;
        status["avg_seconds"] = Math.Round(avg, 1);
        status["phase"] = recovering ? "recheck" : "first_pass";
        try
        {
            var tmp = statusPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tmp, statusPath, true);
        }
        catch { }
    }

    static void Pause(double lo, double hi) => Thread.Sleep((int)((lo + rnd.NextDouble() * (hi - lo)) * 1000));

    // MARK: 窗口、截图、认字

    static void RefreshWin()
    {
        if (!Platform.Exists(wx)) throw new StopError("公众号主页的窗口被关掉了，已停下（请重新打开公众号主页后再开始）");
        win = Platform.Bounds(wx);
        WinW = win.W; WinH = win.H;
        Scale = Platform.DpiScale(wx);
    }

    /// 截窗口里的一块（窗口内坐标）
    static Shot Grab(Box local) => Platform.Capture(new Box(win.X + local.X, win.Y + local.Y, local.W, local.H), local);

    static List<TextBox> Snap(Box? local = null)
    {
        RefreshWin();
        var r = local ?? new Box(0, 0, win.W, win.H);
        using var s = Grab(r);
        return Platform.Recognize(s.Bitmap, r);
    }

    static string Joined(List<TextBox> boxes) => string.Concat(boxes.OrderBy(b => b.Rect.X).Select(b => b.Text));

    // MARK: 标签栏：当前标签、「···」、「×」

    /// 当前标签（窗口内坐标）。鼠标停着的标签也会变灰，所以把鼠标所在的列告诉 Vision，让它别选那个
    static Box? ActiveTab()
    {
        RefreshWin();
        using var s = Grab(new Box(0, 0, win.W, BandH));
        var c = Platform.Cursor();
        double? avoid = c.Y >= win.Y && c.Y < win.Y + BandH ? c.X - win.X : null;
        return Vision.ActiveTab(s.Luma, s.Width, s.Height, Scale, avoid);
    }

    /// 标签上「···」和「×」的中心
    static (double Dots, double Close, double DotsLeft, bool Found) Buttons(Box tab)
    {
        using var s = Grab(new Box(0, 0, win.W, BandH));
        return Vision.TabButtons(s.Luma, s.Width, s.Height, tab);
    }

    /// 鼠标在标签栏上时挪到页面中间：停在标签上会让那个标签变灰、冒出「×」，看不准
    static void Park()
    {
        var c = Platform.Cursor();
        if (c.Y >= win.Y - S(4) && c.Y < win.Y + BandH + S(10)) { Move(win.W * 0.5, win.H * 0.6); Thread.Sleep(150); }
    }

    /// 标签刚打开时大小还在变（有动画），连着两次量到一样的才算数
    static Box SettledTab(Box tab)
    {
        var last = tab;
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(150);
            if (ActiveTab() is not Box t || Math.Abs(t.X - tab.X) > S(40)) continue;
            if (Math.Abs(t.W - last.W) <= S(2) && Math.Abs(t.X - last.X) <= S(2)) return t;
            last = t;
        }
        return last;
    }

    /// 标签上的字（文章标题会被截断，公众号主页就是公众号名字）；去掉开头图标被认成的符号
    static string TabTitle(Box tab, double dotsLeft)
    {
        var t = Joined(Snap(Vision.TitleArea(tab, dotsLeft))).Trim();
        return Regex.Replace(t, @"^[^\p{IsCJKUnifiedIdeographs}A-Za-z0-9]+", "").Trim();
    }

    // MARK: 诊断截图

    static void Diag(string reason, IEnumerable<string>? extra = null)
    {
        try
        {
            Directory.CreateDirectory(diagDir);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{new string(reason.Where(char.IsLetterOrDigit).Take(16).ToArray())}";
            Bitmap bmp;
            List<TextBox> boxes = [];
            if (wx != IntPtr.Zero && Platform.Exists(wx))
            {
                var b = Platform.Bounds(wx);
                using var s = Platform.Capture(b, new Box(0, 0, b.W, b.H));
                bmp = (Bitmap)s.Bitmap.Clone();
                try { boxes = Platform.Recognize(s.Bitmap, new Box(0, 0, b.W, b.H)); } catch { }
            }
            else
            {
                var sb = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using var s = Platform.Capture(new Box(sb.X, sb.Y, sb.Width, sb.Height), new Box(0, 0, sb.Width, sb.Height));
                bmp = (Bitmap)s.Bitmap.Clone();
            }
            using (bmp) bmp.Save(Path.Combine(diagDir, name + ".png"), ImageFormat.Png);
            var txt = new StringBuilder();
            txt.AppendLine($"原因：{reason}");
            txt.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  缩放：{Scale:F2}  滚轮一格：{pxPerNotch:F0}  识别语言：{Platform.OcrLanguage}");
            txt.AppendLine($"系统：{Environment.OSVersion}  上次认出的主页标签：{lastProfileTab}");
            try { txt.AppendLine($"现在认出的当前标签：{(wx != IntPtr.Zero && Platform.Exists(wx) ? ActiveTab() : null)}"); } catch { }
            if (extra != null) foreach (var e in extra) txt.AppendLine(e);
            txt.AppendLine("---- 微信窗口 ----");
            foreach (var w in Platform.Windows().Where(Platform.IsWeChat))
                txt.AppendLine($"{w.ProcessName} 「{w.Title}」 类名={w.Class} 位置=({w.Bounds.X},{w.Bounds.Y} {w.Bounds.W}x{w.Bounds.H}) 最小化={w.Minimized} {(w.Handle == wx ? "← 公众号窗口" : "")}");
            txt.AppendLine("---- 认出的字（窗口内坐标）----");
            foreach (var b in boxes) txt.AppendLine($"({b.Rect.X,5:F0},{b.Rect.Y,5:F0} {b.Rect.W,4:F0}x{b.Rect.H,3:F0}) {b.Text}");
            File.WriteAllText(Path.Combine(diagDir, name + ".txt"), txt.ToString(), new UTF8Encoding(false));
            // 只留最近 40 份（每份一张图一个说明）
            foreach (var old in new DirectoryInfo(diagDir).GetFiles().OrderByDescending(f => f.CreationTimeUtc).Skip(80)) old.Delete();
            Log($"已存诊断截图：诊断\\{name}.png（遇到问题时把「诊断」文件夹发给作者）");
        }
        catch (Exception e) { Log($"存诊断截图失败：{e.Message}"); }
    }

    // MARK: 鼠标键盘

    static bool AtStopCorner(Platform.POINT c)
    {
        var p = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return c.X < p.X + 8 && c.Y < p.Y + 8;
    }

    /// 你一动鼠标或切走微信，就暂停；鼠标甩到主屏幕左上角或收到停止指令，就彻底停
    static void CheckUser()
    {
        if (File.Exists(stopPath)) throw new StopError("收到停止指令，已停止", true);
        var c = Platform.Cursor();
        if (AtStopCorner(c)) throw new StopError("你把鼠标甩到了屏幕左上角，已彻底停止", true);
        double Dist(Platform.POINT a) => Math.Sqrt(Math.Pow(a.X - lastPos.X, 2) + Math.Pow(a.Y - lastPos.Y, 2));
        if (Dist(c) > S(25)) { Thread.Sleep(150); c = Platform.Cursor(); }
        if (Dist(c) > S(25)) throw new Interrupted("你动了鼠标");
        if (!Platform.WeChatIsFront()) throw new Interrupted("微信被切到后面了");
    }

    /// 等你停手：键盘鼠标都静止 5 秒再继续
    static void WaitForIdle()
    {
        while (true)
        {
            if (File.Exists(stopPath)) throw new StopError("收到停止指令，已停止", true);
            if (AtStopCorner(Platform.Cursor())) throw new StopError("你把鼠标甩到了屏幕左上角，已彻底停止", true);
            if (Platform.IdleSeconds() >= 5) return;
            Thread.Sleep(300);
        }
    }

    static void Move(double x, double y)
    {
        CheckUser();
        RefreshWin();
        int sx = (int)Math.Round(win.X + x), sy = (int)Math.Round(win.Y + y);
        Platform.MoveTo(sx, sy);
        lastPos = new Platform.POINT { X = sx, Y = sy };
    }

    static void Click(double x, double y)
    {
        Move(x, y);
        Pause(0.08, 0.15);
        Platform.LeftClick();
    }

    /// 往下滚 px 像素；为负就往上滚。滚轮一格多少像素开始时量过，量不出来按 100 点算
    static void Scroll(double x, double y, double px)
    {
        Move(x, y);
        Thread.Sleep(100);
        double per = pxPerNotch > 0 ? pxPerNotch : S(100);
        int total = (int)Math.Round(Math.Abs(px) / per * 120);
        int sign = px > 0 ? -1 : 1;
        while (total > 0)
        {
            int d = Math.Min(120, total);
            Platform.Wheel(sign * d);
            total -= d;
            Thread.Sleep(35);
        }
    }

    /// 量一下滚轮一格页面移动多少像素：往下滚一格，看一行字移了多少，再滚回来
    static void CalibrateScroll(List<TextBox> boxes)
    {
        var top = ClickableRange(boxes, ContentTop).Top;
        var refBox = boxes.Where(b => b.Rect.Y > top + S(120) && b.Rect.Y < win.H * 0.7 && Normalize(b.Text).Length >= 4)
            .OrderBy(b => b.Rect.Y).FirstOrDefault();
        if (refBox == null) return;
        Move(refBox.Rect.X + S(60), win.H * 0.55);
        Thread.Sleep(100);
        Platform.Wheel(-120);
        Thread.Sleep(700);
        var key = Normalize(refBox.Text);
        var moved = Snap().Where(b => Math.Abs(b.Rect.X - refBox.Rect.X) < S(15) && EditDistance(Normalize(b.Text), key) <= 1)
            .Select(b => refBox.Rect.Y - b.Rect.Y).Where(d => d > 5).DefaultIfEmpty(0).Min();
        Platform.Wheel(120);
        Thread.Sleep(600);
        if (moved > 5) { pxPerNotch = moved; Log($"量了一下：滚轮一格约 {moved:F0} 像素"); }
        else Log("没量出滚轮一格多少像素，按默认值翻页，翻完会自动校准");
    }

    // MARK: 找公众号主页

    /// 从前往后看每个微信窗口，找停在公众号主页的那个
    static void FindProfileWindow()
    {
        var cands = Platform.Windows().Where(w => Platform.IsWeChat(w) && w.Bounds.W > S(500) && w.Bounds.H > S(400)).ToList();
        if (cands.Count == 0) throw new StopError("没找到微信窗口：请先打开微信，进入要采的公众号主页，再点开始");
        foreach (var c in cands)
        {
            wx = c.Handle;
            if (Platform.Foreground() != wx) { Platform.BringToFront(wx); Thread.Sleep(800); }
            RefreshWin();
            if (IsProfilePage(Snap())) return;
        }
        wx = cands[0].Handle;
        Diag("没找到公众号主页");
        throw new StopError("没找到公众号主页：请在微信里打开要采的公众号，停在有「全部 / 文章」分栏的那一页，再点开始");
    }

    /// 确认停在公众号主页，返回主页标签和整屏认字结果；右边开着文章（比如上次没关掉的）就先关掉
    static (Box Tab, List<TextBox> Boxes) EnsureProfile()
    {
        int noTab = 0;
        for (int i = 0; i < 6; i++)
        {
            RefreshWin();
            if (Platform.Foreground() != wx) { Platform.BringToFront(wx); Thread.Sleep(500); }
            if (ActiveTab() is not Box tab)
            {
                // 刚切回来、窗口还在动画，或者有菜单挡着：收起菜单、把窗口拉到前面再看几次
                if (++noTab >= 3)
                {
                    Diag("认不出标签栏");
                    throw new StopError("认不出微信的标签栏：请确认微信停在公众号主页；如果电脑是深色模式，请先切到浅色模式");
                }
                Platform.Key(Platform.VK_ESCAPE);
                Platform.BringToFront(wx);
                Thread.Sleep(800);
                continue;
            }
            var boxes = Snap();
            if (IsProfilePage(boxes)) { lastProfileTab = tab; return (tab, boxes); }
            // 只关「主页标签右边」的标签：那是从主页点开的文章。认不准的一律不关，免得把主页或别的标签关掉
            if (lastProfileTab is Box p && tab.X > p.X + S(60))
            {
                Log("发现开着一篇文章，先关掉它");
                CloseTab(tab, p);
            }
            else
            {
                Platform.Key(Platform.VK_ESCAPE);   // 可能有菜单没收起来
                Thread.Sleep(600);
            }
        }
        Diag("不像公众号主页");
        throw new StopError("当前页面不像公众号主页，已停下（请切回公众号主页后再开始）");
    }

    /// 新开一个文件从头采：先翻回主页最顶上（看到「xx篇原创内容」那行为止）
    static void ScrollToTop()
    {
        EnsureProfile();
        bool AtTop() => Snap(new Box(0, 0, win.W, win.H * 0.5)).Any(b => b.Text.Replace(" ", "").Contains("篇原创"));
        if (AtTop()) return;
        Move(win.W * 0.5, win.H * 0.55);
        Platform.Key(Platform.VK_HOME);
        Thread.Sleep(800);
        for (int i = 0; i < 80; i++)
        {
            if (AtTop()) return;
            Scroll(win.W * 0.5, win.H * 0.55, -S(6000));
            Thread.Sleep(300);
        }
        throw new StopError("翻不回主页顶上，已停下");
    }

    /// 公众号名字：主页标签上的字；认不出就用页面顶上第一行字
    static string AccountName(Box tab, List<TextBox> boxes)
    {
        static string Clean(string t) => SafeFileName(Regex.Replace(t, @"^[^\p{IsCJKUnifiedIdeographs}A-Za-z0-9]+", ""));
        Park();
        var name = Clean(TabTitle(tab, Buttons(tab).DotsLeft));
        if (Normalize(name).Length >= 2 && !IsPlaceholder(name)) return name;
        var header = boxes.Where(b => b.Rect.Y > tab.Bottom && b.Rect.Y < tab.Bottom + S(160))
            .Where(b => { var n = Normalize(b.Text); return n.Length >= 2 && !b.Text.Contains("全部") && !n.Contains("关注") && !n.Contains("篇原创"); })
            .OrderBy(b => b.Rect.Y).FirstOrDefault();
        return header != null ? Clean(header.Text) : "";
    }

    static string PickMdName(string name)
    {
        if (Normalize(name).Length < 2) return "文章链接.md";
        var n = Normalize(name);
        var hit = Directory.GetFiles(dataDir, "*.md").Select(Path.GetFileName).FirstOrDefault(f =>
        {
            var s = Normalize(Path.GetFileNameWithoutExtension(f!));
            return s == n || (Math.Min(s.Length, n.Length) >= 4 && EditDistance(s, n) <= 1);
        });
        return hit ?? name + ".md";
    }

    // MARK: 一篇文章：点开 →「···」→ 复制链接 → 关掉

    record Result(string? Url, bool Loaded, bool Matched, string Opened = "", string OpenedTitle = "");

    static bool toldNotOpened;

    static Result CopyLink(Card card, Box profileTab)
    {
        // 点卡片，等文章在主页标签右边打开成新的当前标签（标签一多会自动变窄，只能比左边起点）。
        // 第一下点击有时只是把微信窗口激活了，没反应就再点一次
        Box? opened = null;
        for (int attempt = 0; attempt < 2 && opened == null; attempt++)
        {
            Click(card.X, card.Y);
            for (int i = 0; i < (attempt == 0 ? 13 : 20); i++)
            {
                Thread.Sleep(200);
                if (ActiveTab() is Box t && t.X > profileTab.X + S(60)) { opened = t; break; }
            }
        }
        if (opened is not Box tab0)
        {
            Platform.Key(Platform.VK_ESCAPE);
            Log($"⚠️ 点了没打开新标签，跳过：{card.Title}");
            if (!toldNotOpened) { toldNotOpened = true; Diag("点了没打开新标签"); }
            return new Result(null, false, false);
        }
        var tab = SettledTab(tab0);
        var buttons = Buttons(tab);

        // 等加载完：标签从「微信公众平台」换成文章标题
        var t0 = DateTime.Now;
        string tabText = "";
        bool toldSlow = false;
        while (true)
        {
            tabText = TabTitle(tab, buttons.DotsLeft);
            if (!IsPlaceholder(tabText)) break;
            double waited = (DateTime.Now - t0).TotalSeconds;
            if (waited > LoadTimeout) break;
            if (waited > 4 && !toldSlow) { Log("网络有点慢，等文章加载出来…"); toldSlow = true; SaveStatus(new() { ["state"] = "running" }); }
            Thread.Sleep(250);
        }
        bool loaded = !IsPlaceholder(tabText);
        bool matched = loaded && SameArticle(tabText, card);
        if (loaded && !matched) Log($"⚠️ 打开的文章和要点的对不上（标签：{tabText}，卡片：{card.Title}）");

        // 点「···」找「复制链接」；还没有就收起菜单等一会儿再点，一直等到加载超时
        double loadWait = (DateTime.Now - t0).TotalSeconds;
        string? url = null;
        int tries = 0;
        bool toldNoMenu = false;
        while (url == null)
        {
            // 每次点之前重新在标签上找「···」（鼠标先挪开，停在标签上会挡住图标）
            Park();
            buttons = Buttons(tab);
            Click(buttons.Dots, tab.MidY);
            tries++;
            TextBox? item = null;
            for (int i = 0; i < 5 && item == null; i++)
            {
                Thread.Sleep(220);
                // 菜单在「···」下面、向左展开（录屏里「复制链接」在「···」左边约 5~7 个标签高、下面约 3 个标签高）
                double x0 = Math.Max(0, buttons.Dots - tab.H * 10), x1 = Math.Min(win.W, buttons.Dots + tab.H * 6);
                var area = new Box(x0, tab.Bottom, x1 - x0, Math.Min(win.H - tab.Bottom, tab.H * 7));
                item = Snap(area).FirstOrDefault(t =>
                {
                    var n = Normalize(t.Text);
                    return n.StartsWith("复制链接") || n.StartsWith("复制网址");
                });
            }
            if (item != null)
            {
                uint before = Platform.ClipboardSeq();
                Click(item.Rect.MidX, item.Rect.MidY);
                for (int i = 0; i < 20 && Platform.ClipboardSeq() == before; i++) Thread.Sleep(100);
                var s = Platform.ClipboardSeq() != before ? Platform.ClipboardText()?.Trim() : null;
                if (s != null && s.StartsWith("http") && s.Contains("mp.weixin.qq.com")) url = s;
                else if (s == null) Log("点了「复制链接」，剪贴板没变化");
                else Log($"剪贴板里不是公众号文章链接：{(s.Length > 60 ? s[..60] : s)}");
                break;
            }
            Platform.Key(Platform.VK_ESCAPE);
            if (!toldNoMenu && loaded) { toldNoMenu = true; Diag("点了「···」没看到「复制链接」", [$"文章标签：{tab}  「···」：{buttons.Dots:F0}（{(buttons.Found ? "认出的" : "推算的")}）"]); }
            if ((DateTime.Now - t0).TotalSeconds > LoadTimeout + 5) break;
            Thread.Sleep(800);
        }
        if (url != null && (loadWait > 3 || tries > 1)) Log($"这篇慢：等加载 {loadWait:F1} 秒，点了 {tries} 次「···」");
        if (url == null) Log(loaded ? $"⚠️ 没拿到链接：{card.Title}" : $"⚠️ 等了 {(int)LoadTimeout} 秒还没加载出来，先跳过，补漏时再试：{card.Title}");

        if (!CloseTab(tab, profileTab))
        {
            Diag("文章标签关不掉");
            throw new StopError("文章标签关不掉，已停下（请手动关掉文章、回到公众号主页后再开始）");
        }
        return new Result(url, loaded, matched, loaded ? Normalize(tabText) : "", loaded ? tabText : "");
    }

    /// 关掉主页标签右边的这个文章标签：点它右端的「×」，确认当前标签回到了左边（主页）。
    /// 每次点之前都确认当前标签还是它，免得点到别的标签；点两次不行再按 Ctrl+W（只关当前这一个标签）
    static bool CloseTab(Box tab, Box profileTab)
    {
        bool StillThere(Box? t) => t is Box a && Math.Abs(a.X - tab.X) <= S(40) && a.X > profileTab.X + S(60);
        bool Back(Box? t) => t is Box a && a.X < tab.X - S(60);
        for (int round = 0; round < 3; round++)
        {
            Park();
            var now = ActiveTab();
            if (Back(now)) return true;
            if (!StillThere(now)) { Thread.Sleep(400); now = ActiveTab(); if (Back(now)) return true; if (!StillThere(now)) return false; }
            var cur = (Box)now!;
            if (round < 2)
            {
                var close = Buttons(cur).Close;
                Move(close, cur.MidY);
                Thread.Sleep(250);
                Click(close, cur.MidY);
            }
            else
            {
                CheckUser();
                if (Platform.WeChatIsFront()) Platform.Chord(Platform.VK_W);
            }
            Thread.Sleep(200);
            Park();
            for (int i = 0; i < 8; i++)
            {
                if (Back(ActiveTab())) return true;
                Thread.Sleep(200);
            }
        }
        return false;
    }

    // MARK: 一屏一屏往下扫

    static bool Sweep()
    {
        int sameCount = 0;
        string lastSignature = "";
        var skipped = new HashSet<string>();
        var openedBy = new Dictionary<string, List<string>>();
        int opensHere = 0;
        double scrollRatio = 1.0;
        (string Text, double X, double Y, double Asked)? anchor = null;
        int backtracks = 0;
        while (true)
        {
            if (added >= maxCount) return false;
            RefreshWin();
            var (profileTab, boxes) = EnsureProfile();
            if (pxPerNotch == 0) { CalibrateScroll(boxes); (profileTab, boxes) = EnsureProfile(); }
            // 翻页前最下面那张卡片的阅读数，翻完应该停在可点区域靠上的位置；找不到说明翻过头了，往回拨一点
            if (anchor is { } a)
            {
                var top = ClickableRange(boxes, ContentTop).Top;
                var key = Normalize(a.Text);
                var found = boxes.FirstOrDefault(b => b.Rect.Y >= top - S(5) && Math.Abs(b.Rect.X - a.X) < S(15)
                    && b.Text.Replace(" ", "").StartsWith("阅读") && EditDistance(Normalize(b.Text), key) <= 1);
                if (found != null)
                {
                    backtracks = 0;
                    double moved = a.Y - found.Rect.Y;
                    if (a.Asked > 0 && moved > 20)
                    {
                        double r = moved / a.Asked;
                        scrollRatio = r > scrollRatio ? 0.3 * scrollRatio + 0.7 * r : 0.8 * scrollRatio + 0.2 * r;
                        scrollRatio = Math.Clamp(scrollRatio, 0.5, 2.5);
                    }
                }
                else if (backtracks < 4)
                {
                    backtracks++;
                    scrollRatio = Math.Min(scrollRatio * 1.3, 2.5);
                    Log("翻过头了（上一屏最下面那篇不见了），往回拨一点，免得漏掉顶上的文章");
                    var visible = ClickableRange(boxes, ContentTop).Bottom - top;
                    Scroll(a.X + S(100), win.H * 0.55, -visible * 0.35);
                    anchor = (a.Text, a.X, a.Y, 0);
                    Pause(0.7, 1.0);
                    continue;
                }
            }
            anchor = null;
            var (cards, notRead) = VisibleCards(boxes, ContentTop);
            foreach (var u in notRead) if (unreadable.Add(u)) Log($"⚠️ 有张卡片认不出标题（{u}），可能会漏，跑完补漏时会再看一次");

            bool redo = false;
            foreach (var card in cards)
            {
                if (rec.SeenIndex(card.Key) is int i) { rec.Cursor = i; continue; }
                if (skipped.Contains(card.Key)) continue;
                if (added >= maxCount) return false;
                // 兜底：一屏点开 10 次还没走完，剩下的先跳过、往下翻，最后补漏时再试
                if (opensHere >= 10)
                {
                    var left = cards.Where(c => rec.SeenIndex(c.Key) == null && !skipped.Contains(c.Key)).ToList();
                    foreach (var c in left) skipped.Add(c.Key);
                    Log($"⚠️ 这一屏点了 {opensHere} 次还没走完，剩下 {left.Count} 张先跳过、接着往下翻，最后补漏时再试");
                    break;
                }
                opensHere++;
                sameCount = 0;
                var t0 = DateTime.Now;
                var r = CopyLink(card, profileTab);
                if (r.Url is not string u) { skipped.Add(card.Key); continue; }
                bool isNew = !rec.Urls.Contains(u);
                if (!openedBy.TryGetValue(card.Key, out var got)) openedBy[card.Key] = got = [];
                got.Add(u);
                bool sameTwice = got.Count(x => x == u) >= 2;
                if (r.Matched || !r.Loaded || sameTwice) rec.Record(card.Key, u, card.Title);
                else
                {
                    rec.Record(r.Opened, u, r.OpenedTitle);
                    redo = true;
                    if (got.Count >= 3) { skipped.Add(card.Key); Log($"⚠️ 这张卡片点了 3 次都对不上，先跳过，补漏时再试：{card.Title}"); }
                }
                var shown = r.Matched || !r.Loaded ? card.Title : r.OpenedTitle;
                if (isNew)
                {
                    added++;
                    if (recovering) recovered++;
                    status["last_title"] = shown;
                    durations.Add((DateTime.Now - t0).TotalSeconds);
                    Log($"{(recovering ? "🔁 补漏" : "✅")} 第 {rec.Urls.Count} 条（{durations[^1]:F1} 秒）：{shown}  {u}");
                    if (added % RestEvery == 0) { Log("歇一会儿…"); Pause(15, 30); }
                }
                else
                {
                    reopened++;
                    Log($"重复，跳过（链接已经有了，不会重复写）：{card.Title}");
                }
                if (redo) break;
                Pause(0.2, 0.4);
            }
            if (redo) continue;

            // 往下翻。翻完画面没变，可能在加载：越等越久，中间往回拨一点再往下催它加载，四次都不动才算到底
            double column = cards.Count > 0 ? cards[0].X : win.W * 0.5;
            var signature = string.Concat(boxes.Where(b => b.Rect.Y > win.H * 0.3).Select(b => b.Text));
            if (signature == lastSignature)
            {
                sameCount++;
                if (sameCount > 4)
                {
                    var bottomText = string.Join(" / ", boxes.OrderBy(b => b.Rect.Y).TakeLast(2).Select(b => b.Text));
                    Log($"连续 4 次往下翻都没有新内容，判断已经到底（最后一屏底部：{bottomText}）");
                    return true;
                }
                double wait = new[] { 2.0, 3.0, 5.0, 8.0 }[sameCount - 1];
                Log($"往下翻没出新内容，可能在加载，等 {(int)wait} 秒再试");
                Pause(wait, wait + 0.5);
                if (sameCount >= 2) { Scroll(column, win.H * 0.55, -S(300)); Thread.Sleep(600); }
            }
            else sameCount = 0;
            lastSignature = signature;
            var plan = PlannedScroll(boxes, ContentTop);
            double ask = plan.Points * 0.9 / scrollRatio;   // 少翻一点：翻不够只是多看一屏，翻过头会漏
            if (plan.Anchor is { } pa) anchor = (pa.Text, pa.Rect.X, pa.Rect.Y, ask);
            Scroll(column, win.H * 0.55, ask);
            opensHere = 0;
            Pause(0.7, 1.0);
        }
    }

    static T Resumable<T>(Func<T> body)
    {
        while (true)
        {
            try { return body(); }
            catch (Interrupted e)
            {
                SaveStatus(new() { ["state"] = "paused" });
                Log($"⏸ {e.Message}，暂停。键盘鼠标静止 5 秒后自动继续（要彻底停止：把鼠标甩到主屏幕左上角）");
                WaitForIdle();
                SaveStatus(new() { ["state"] = "running" });
                Log("▶️ 继续");
                Platform.BringToFront(wx);
                Thread.Sleep(600);
                RefreshWin();
                lastPos = Platform.Cursor();
            }
        }
    }

    // MARK: 入口

    public static int Run(string[] argv)
    {
        var args = argv.ToList();
        string? Take(string flag)
        {
            int i = args.IndexOf(flag);
            if (i < 0 || i + 1 >= args.Count) return null;
            var v = args[i + 1];
            args.RemoveRange(i, 2);
            return v;
        }
        bool Flag(string f) => args.Remove(f);
        if (int.TryParse(Take("--max"), out var m)) maxCount = m;
        runId = Take("--run-id") ?? "";
        dataDir = Take("--dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "公众号链接采集");
        mdName = Take("--file");
        recheckOnly = Flag("--recheck");
        probeMode = Flag("--probe");
        bool ocrTest = Flag("--ocr-test");
        if (mdName != null && !mdName.EndsWith(".md")) mdName += ".md";

        stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Directory.CreateDirectory(dataDir);
        logPath = Path.Combine(dataDir, "运行日志.txt");
        statusPath = Path.Combine(dataDir, "status.json");
        pidPath = Path.Combine(dataDir, ".running.pid");
        stopPath = Path.Combine(dataDir, ".stop");
        diagDir = Path.Combine(dataDir, "诊断");
        Platform.InitDpi();

        if (ocrTest) return OcrTest();
        if (probeMode) return Probe();

        // 同一时间只跑一个
        if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath).Trim(), out var oldPid) && oldPid != Environment.ProcessId)
        {
            try { if (!System.Diagnostics.Process.GetProcessById(oldPid).HasExited) { stdout.WriteLine($"已经有一个在跑了（进程 {oldPid}），这次不启动"); return 0; } } catch { }
        }
        File.WriteAllText(pidPath, Environment.ProcessId.ToString());
        try { File.Delete(stopPath); } catch { }
        status["state"] = "starting"; status["pid"] = Environment.ProcessId; status["run_id"] = runId; status["started_at"] = DateTime.UtcNow.ToString("o");
        SaveStatus(new() { ["message"] = "正在启动" });

        var savedClipboard = Platform.ClipboardText();
        string finalState = "finished";
        var start = DateTime.Now;
        try
        {
            if (!Platform.InitOcr())
            {
                finalState = "needs_permission";
                throw new StopError("这台电脑没有中文文字识别：请在 设置 → 时间和语言 → 语言和区域 里添加「中文(简体，中国)」，装好后再开始（应用的「设置与检查」页有详细步骤）");
            }
            if (!Platform.WeChatRunning()) throw new StopError("微信没有打开：请先打开微信，进入要采的公众号主页，再点开始");
            SaveStatus(new() { ["state"] = "waiting_idle" });
            Log("等你松开鼠标键盘 5 秒后开始…");
            WaitForIdle();
            SaveStatus(new() { ["state"] = "running" });
            lastPos = Platform.Cursor();
            FindProfileWindow();
            lastPos = Platform.Cursor();
            var (tab, boxes) = Resumable(() => { RefreshWin(); lastPos = Platform.Cursor(); return EnsureProfile(); });
            account = AccountName(tab, boxes);
            mdName ??= PickMdName(account);
            rec = Records.Load(Path.Combine(dataDir, mdName));
            Log($"开始采集「{account}」→ {rec.MdPath}（已有 {rec.Urls.Count} 条，已点过 {rec.Entries.Count} 篇）。误碰鼠标会暂停、停手 5 秒自动继续；鼠标甩到主屏幕左上角彻底停止。");
            bool reachedEnd = false, lookedBack = false;
            if (!recheckOnly)
            {
                reachedEnd = Resumable(() =>
                {
                    if (rec.Entries.Count == 0) { ScrollToTop(); rec.Cursor = -1; }
                    else if (!lookedBack)
                    {
                        // 接着上次采：先往回翻一点，上次停下时顶上要是有漏掉的，这次能补上（点过的会直接跳过）
                        lookedBack = true;
                        EnsureProfile();
                        Log("接着上次采：先往回翻一点，看看停下的地方有没有漏的");
                        Scroll(win.W * 0.5, win.H * 0.55, -win.H * 0.6);
                        Pause(0.7, 1.0);
                    }
                    return Sweep();
                });
            }
            if (recheckOnly || reachedEnd)
            {
                Log("从头再扫一遍，补漏掉的…");
                recovering = true;
                rec.StrictSeen = true;
                bool atTop = false;
                if (Resumable(() => { if (!atTop) { ScrollToTop(); atTop = true; rec.Cursor = -1; } return Sweep(); })) Log("补漏扫完了。");
            }
            if (added >= maxCount) Log($"达到本次上限 {maxCount} 篇。");
        }
        catch (StopError e)
        {
            Log(e.Message);
            status["reason"] = e.Message;
            if (finalState != "needs_permission") finalState = e.ByUser ? "stopped" : "error";
        }
        catch (Exception e)
        {
            Log($"出错：{e.Message}");
            status["reason"] = e.ToString();
            finalState = "error";
            Diag("程序出错", [e.ToString()]);
        }
        if (savedClipboard != null) Platform.SetClipboardText(savedClipboard);
        try { File.Delete(pidPath); File.Delete(stopPath); } catch { }
        double total = (DateTime.Now - start).TotalSeconds;
        double avg = durations.Count == 0 ? 0 : durations.Average();
        Log($"本次新增 {added} 条（其中补漏 {recovered} 条），白开 {reopened} 次，认不出标题的卡片 {unreadable.Count} 张；用时 {total:F0} 秒（每篇平均 {avg:F1} 秒），文件里共 {rec.Urls.Count} 条：{rec.MdPath}");
        SaveStatus(new() { ["state"] = finalState, ["finished_at"] = DateTime.UtcNow.ToString("o") });
        return 0;
    }

    /// 只看不点：找公众号窗口、认字、认卡片、认标签栏，结果打印出来并存一份诊断截图
    static int Probe()
    {
        var o = new StringBuilder();
        try
        {
            o.AppendLine($"系统：{Environment.OSVersion}");
            o.AppendLine($"电脑上能用的文字识别语言：{string.Join("、", Platform.OcrLanguages())}");
            if (!Platform.InitOcr()) { o.AppendLine("❌ 没有中文文字识别：在 设置 → 时间和语言 → 语言和区域 里添加「中文(简体，中国)」"); return Done(); }
            o.AppendLine($"✅ 中文文字识别：{Platform.OcrLanguage}");
            if (!Platform.WeChatRunning()) { o.AppendLine("❌ 微信没有打开"); return Done(); }
            foreach (var w in Platform.Windows().Where(Platform.IsWeChat))
                o.AppendLine($"微信窗口：{w.ProcessName} 「{w.Title}」 {w.Bounds.W}x{w.Bounds.H} {(w.Minimized ? "（最小化）" : "")}");
            lastPos = Platform.Cursor();
            try { FindProfileWindow(); }
            catch (StopError e) { o.AppendLine("❌ " + e.Message); return Done(); }
            o.AppendLine($"✅ 找到公众号主页窗口：{win.W}x{win.H}，屏幕缩放 {Scale * 100:F0}%");
            var tab = ActiveTab();
            if (tab is Box t)
            {
                var b = Buttons(t);
                lastProfileTab = t;
                o.AppendLine($"✅ 当前标签：左 {t.X:F0} 右 {t.Right:F0} 高 {t.H:F0}；「···」在 {b.Dots:F0}，「×」在 {b.Close:F0}（{(b.Found ? "认出的" : "按位置推算的")}）");
                o.AppendLine($"标签上的字：{TabTitle(t, b.DotsLeft)}");
            }
            else o.AppendLine("❌ 认不出当前标签（标签栏要是浅色的；鼠标别停在标签上）");
            var boxes = Snap();
            if (tab is Box t2) o.AppendLine($"公众号名字：{AccountName(t2, boxes)}");
            var (cards, notRead) = VisibleCards(boxes, ContentTop);
            o.AppendLine($"这一屏认出 {cards.Count} 张卡片：");
            foreach (var c in cards) o.AppendLine($"  ({c.X:F0},{c.Y:F0}) {c.Title}");
            foreach (var u in notRead) o.AppendLine($"  认不出标题：{u}");
            var (top, bottom) = ClickableRange(boxes, ContentTop);
            var plan = PlannedScroll(boxes, ContentTop);
            o.AppendLine($"可点区域：{top:F0}-{bottom:F0}，计划翻页 {plan.Points:F0} 像素");
            o.AppendLine(tab != null && cards.Count > 0 ? "✅ 看起来没问题，可以开始采集" : "⚠️ 有地方没认出来，请把诊断截图发给作者");
            Diag("检查", o.ToString().Split('\n'));
        }
        catch (Exception e) { o.AppendLine($"出错：{e}"); }
        return Done();

        int Done() { stdout.Write(o.ToString()); return 0; }
    }

    /// 自检文字识别（不碰微信）：画几行和微信界面里一样的字，用系统识别认一遍，打印认出的字和位置。云端编译时用来确认识别能用
    static int OcrTest()
    {
        Scale = 1;
        stdout.WriteLine($"能用的文字识别语言：{string.Join("、", Platform.OcrLanguages())}");
        if (!Platform.InitOcr()) { stdout.WriteLine("没有中文文字识别"); return 2; }
        string[] lines = ["公众号名字", "全部    贴图    文章", "星期日", "用 AI 整理文章链接的小工具", "阅读 101", "复制链接", "36篇原创内容"];
        using var bmp = new Bitmap(520, 40 + lines.Length * 44);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Microsoft YaHei UI", 11f);
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], font, Brushes.Black, 20, 20 + i * 44);
        }
        var boxes = Platform.Recognize(bmp, new Box(0, 0, bmp.Width, bmp.Height));
        foreach (var b in boxes) stdout.WriteLine($"({b.Rect.X:F0},{b.Rect.Y:F0} {b.Rect.W:F0}x{b.Rect.H:F0}) {b.Text}");
        bool ok = boxes.Any(b => Normalize(b.Text).StartsWith("复制链接")) && boxes.Any(b => b.Text.Replace(" ", "").StartsWith("阅读"));
        stdout.WriteLine(ok ? "识别正常" : "识别结果不对");
        return ok ? 0 : 1;
    }
}
