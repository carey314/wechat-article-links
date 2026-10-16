using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WXLinks.Engine;

/// 一张文章卡片。Lines：卡片里每行字，用来核对打开的对不对
public record Card(string Key, string Title, double X, double Y, List<string> Lines);

/// 不碰系统的纯逻辑：认卡片、算翻页、比标题、记录文件。和 Mac 版（Sources/engine.swift）同一套规则
public static class Core
{
    /// 屏幕缩放（1 = 100%）。Mac 版的距离按「点」写，这里乘上缩放变成像素
    public static double Scale = 1;
    public static double S(double v) => v * Scale;
    public static double WinW = 1200, WinH = 900;

    /// 标题只留字母数字（含汉字）取前 18 个；认字常把 I 认成 l，统一成 i
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant().Replace('l', 'i'))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            if (sb.Length >= 18) break;
        }
        return sb.ToString();
    }

    public static int EditDistance(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cur = row[j];
                row[j] = a[i - 1] == b[j - 1] ? prev : Math.Min(prev, Math.Min(row[j], row[j - 1])) + 1;
                prev = cur;
            }
        }
        return row[b.Length];
    }

    /// 刚点开文章时，标签先显示「微信公众平台」「正在加载」之类的占位，加载完才换成文章标题；认字常认错一两个字
    public static bool IsPlaceholder(string tabText)
    {
        var n = Normalize(tabText);
        return n.Length == 0 || n == "微信" || n.Contains("微信公众平") || n.StartsWith("正在加") || tabText.Contains("加载");
    }

    /// 卡片顶上的日期：「今天」「昨天」「星期一」「9月23日」「2025年12月3日」
    static readonly Regex DateRe = new(@"^(星期[一二三四五六日天]|(\d{4}年)?\d{1,2}月\d{1,2}日)$");
    public static bool IsDateLabel(string s)
    {
        var t = s.Replace(" ", "").Trim();
        return t == "今天" || t == "昨天" || DateRe.IsMatch(t);
    }

    static bool IsStat(string s) => s.Replace(" ", "").StartsWith("阅读");

    /// 主页的分栏：「全部」后面跟着「贴图 / 文章 / 视频号」里的几个（各个号不一样），或者在最顶上能看到「xx篇原创内容」
    public static bool IsProfilePage(List<TextBox> boxes)
    {
        var top = boxes.Where(b => b.Rect.Y < S(420)).ToList();
        string[] others = ["文章", "视频号", "贴图"];
        bool tabsRow = top.Any(a => a.Text.Contains("全部") && (others.Any(a.Text.Contains)
            || top.Any(b => others.Any(b.Text.Contains) && Math.Abs(b.Rect.MidY - a.Rect.MidY) < S(10) && b.Rect.X > a.Rect.X)));
        return tabsRow || top.Any(b => b.Text.Replace(" ", "").Contains("篇原创"));
    }

    /// 文章页：标题下面有「2026年10月15日」这样的日期
    static readonly Regex FullDateRe = new(@"\d{4}年\d{1,2}月\d{1,2}日");
    public static bool LooksLikeArticle(List<TextBox> boxes) =>
        boxes.Any(b => b.Rect.Y < S(260) && FullDateRe.IsMatch(b.Text.Replace(" ", "")));

    /// 可点区域：顶上吸住的「全部 贴图 文章 视频号」下面 20 点，到窗口底 8 点（坐标都是窗口内）
    public static (double Top, double Bottom) ClickableRange(List<TextBox> boxes, double contentTop)
    {
        string[] sticky = ["全部", "文章", "视频号", "贴图"];
        var hits = boxes.Where(b => sticky.Any(b.Text.Contains) && b.Rect.Y < contentTop + S(160)).ToList();
        double top = (hits.Count > 0 ? hits.Max(b => b.Rect.Bottom) : contentTop + S(25)) + S(20);
        return (top, WinH - S(8));
    }

    /// 把同一行的几段字按左右顺序拼起来（认字常把一行标题拆成几段）
    public static string RowText(List<TextBox> boxes, TextBox first, double left) =>
        string.Concat(boxes.Where(b => Math.Abs(b.Rect.MidY - first.Rect.MidY) < S(6) && b.Rect.X >= left - S(15) && b.Rect.X < left + S(420))
            .OrderBy(b => b.Rect.X).Select(b => b.Text));

    /// 列表里完整可见的卡片，以「阅读…」那一行为锚点：普通文章标题在阅读数正上方；文字帖标题是日期下面第一行。
    /// 返回认出的卡片，和完整露出来却认不出标题的卡片（可能会漏）
    public static (List<Card> Cards, List<string> Unreadable) VisibleCards(List<TextBox> boxes, double contentTop)
    {
        double top = ClickableRange(boxes, contentTop).Top;
        var cards = new List<Card>();
        var unreadable = new List<string>();
        foreach (var stat in boxes.Where(b => IsStat(b.Text) && b.Rect.Bottom <= WinH - S(8) && b.Rect.Y > top))
        {
            double left = stat.Rect.X;
            var above = boxes.Where(b => Math.Abs(b.Rect.X - left) < S(15) && b.Rect.Bottom <= stat.Rect.Y + S(2) && b.Rect.Y >= stat.Rect.Y - S(420))
                .OrderByDescending(b => b.Rect.Y).ToList();
            int cut = above.FindIndex(b => IsStat(b.Text));
            if (cut >= 0) above = above.Take(cut).ToList();
            var date = above.FirstOrDefault(b => IsDateLabel(b.Text));
            if (date != null) above = above.Where(b => b.Rect.Y > date.Rect.Bottom).ToList();

            TextBox? title = null;
            string tail = "";   // 标题折成两行、第二行很短（比如「更新」）时，接在第一行后面
            if (above.Count > 0 && above[0].Rect.Y >= stat.Rect.Y - S(45))
            {
                var nearText = RowText(boxes, above[0], left);
                if (Normalize(nearText).Length >= 6) title = above[0];
                else if (!nearText.Contains("展开") && !nearText.Contains("收起") && above.Count > 1
                         && above[1].Rect.Bottom >= above[0].Rect.Y - S(12) && Normalize(RowText(boxes, above[1], left)).Length >= 6)
                {
                    title = above[1];
                    tail = nearText;
                }
            }
            if (title == null && date != null && above.Count > 0) title = above[^1];   // 日期下面第一行
            if (title == null)
            {
                // 只在整张卡片都露出来（看得到顶上的日期）时才提醒；上半截被挡住的，上一屏已经完整露过
                if (date != null) unreadable.Add($"{stat.Text}，在窗口第 {(int)stat.Rect.Y} 像素");
                continue;
            }
            if (title.Rect.Y < top) continue;   // 标题被顶上挡住了，上一屏已经完整露过
            var text = RowText(boxes, title, left) + tail;
            if (Normalize(text).Length < 4) { unreadable.Add($"{stat.Text}，在窗口第 {(int)stat.Rect.Y} 像素"); continue; }
            var lines = above.Select(b => Normalize(RowText(boxes, b, left))).Where(s => s.Length >= 4).ToList();
            cards.Add(new Card(Normalize(text), text, title.Rect.X + Math.Min(title.Rect.W / 2, S(120)), title.Rect.MidY, lines));
        }
        return (cards.OrderBy(c => c.Y).ToList(), unreadable);
    }

    /// 这一屏处理完以后往下翻多少：让最下面那张完整露出来的卡片，翻完后阅读数停在可点区域顶上 40 点处
    public static (double Points, TextBox? Anchor) PlannedScroll(List<TextBox> boxes, double contentTop)
    {
        var (top, bottom) = ClickableRange(boxes, contentTop);
        double visible = bottom - top;
        var stats = boxes.Where(b => IsStat(b.Text) && b.Rect.Bottom <= bottom && b.Rect.Y > top).ToList();
        if (stats.Count == 0) return (visible * 0.5, null);
        var low = stats.MaxBy(b => b.Rect.Bottom)!;
        return (Math.Min(Math.Max(low.Rect.Bottom - (top + S(40)), S(80)), visible * 0.9), low);
    }

    /// 标签上的标题（会被截断）和卡片是不是同一篇：对得上卡片里任意一行就算
    public static bool SameArticle(string tabText, Card card)
    {
        var t = Normalize(tabText);
        if (t.Length > 6) t = t[..6];
        if (t.Length < 4) return false;
        return new[] { card.Key }.Concat(card.Lines).Any(l => EditDistance(t, l.Length > t.Length ? l[..t.Length] : l) <= 1);
    }

    /// 一行里的网址：不管外面套了尖括号、列表符号还是 Markdown 链接
    static readonly Regex UrlRe = new(@"https?://[^\s<>()\[\]""]+");
    public static string? ExtractUrl(string line) { var m = UrlRe.Match(line); return m.Success ? m.Value : null; }

    /// 公众号名字里不能用作文件名的字去掉
    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat("/\\:*?\"<>|.".ToCharArray()).ToHashSet();
        return new string(name.Where(c => !bad.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
    }
}

/// 按列表顺序记下每张点过的卡片：「叫法们」+ 链接 + 标题。md 由它按顺序生成，补漏补到的会插回原位。
/// 文件格式和 Mac 版一样：每行「叫法1|叫法2<TAB>链接<TAB>标题」
public class Records
{
    public class Entry
    {
        public List<string> Keys = [];
        public string Url = "";
        public string Title = "";
    }

    public List<Entry> Entries = [];
    public List<string> MdOnly = [];   // md 里有、记录里没有的链接（比如手动加的），保留在最前面
    public HashSet<string> Urls = [];
    public bool AngleStyle;            // md 被编辑器整理成了「<链接>  」，写回时保持这个格式
    public bool StrictSeen;            // 补漏那遍：只认标题一样或前缀相同，宁可多点开
    public int? Cursor;                // 扫到列表哪儿了：上一张经过的卡片在 Entries 里的位置。-1 表示从顶上开始
    public string MdPath = "", StatePath = "";

    public static Records Load(string mdPath)
    {
        var r = new Records { MdPath = mdPath, StatePath = mdPath + ".已点过.txt" };
        var raw = File.Exists(r.StatePath) ? File.ReadAllText(r.StatePath) : "";
        var list = new List<Entry>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var f = line.TrimEnd('\r').Split('\t', 3);
            var e = new Entry
            {
                Keys = f[0].Split('|').Where(k => k.Length > 0 && !Core.IsPlaceholder(k)).ToList(),
                Url = f.Length > 1 ? f[1] : "",
                Title = f.Length > 2 ? f[2] : "",
            };
            if (!Core.IsPlaceholder(e.Title))
            {
                var k = Core.Normalize(e.Title);
                if (k.Length >= 4 && !e.Keys.Contains(k)) e.Keys.Add(k);
            }
            list.Add(e);
        }
        // 同一个链接的多条合并成一条（旧版会留下重复）
        var firstAt = new Dictionary<string, int>();
        foreach (var e in list)
        {
            if (e.Url.Length > 0 && firstAt.TryGetValue(e.Url, out var i))
            {
                foreach (var k in e.Keys) if (!r.Entries[i].Keys.Contains(k)) r.Entries[i].Keys.Add(k);
                if (Core.IsPlaceholder(r.Entries[i].Title) && !Core.IsPlaceholder(e.Title)) r.Entries[i].Title = e.Title;
                continue;
            }
            if (e.Url.Length > 0) firstAt[e.Url] = r.Entries.Count;
            r.Entries.Add(e);
        }
        var md = File.Exists(mdPath) ? File.ReadAllText(mdPath) : "";
        var mdLines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        r.AngleStyle = mdLines.Any(l => l.StartsWith("<http"));
        var known = r.Entries.Select(e => e.Url).ToHashSet();
        r.MdOnly = mdLines.Select(Core.ExtractUrl).Where(u => u != null && !known.Contains(u!)).Select(u => u!).Distinct().ToList();
        r.Urls = r.Entries.Select(e => e.Url).Where(u => u.Length > 0).Concat(r.MdOnly).ToHashSet();
        return r;
    }

    /// 点过没有：标题一样，或者一个是另一个的开头（认字时标题被截短）；第一遍还容忍认错一两个字
    public int? SeenIndex(string key)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            foreach (var old in Entries[i].Keys)
            {
                if (old == key) return i;
                int n = Math.Min(old.Length, key.Length);
                if (n >= 10 && (old.StartsWith(key) || key.StartsWith(old))) return i;
                if (StrictSeen) continue;
                if (n >= 12 && Math.Abs(old.Length - key.Length) <= 2 && Core.EditDistance(old, key) <= (n >= 15 ? 2 : 1)) return i;
            }
        }
        return null;
    }

    /// 记下一篇：链接已经记过就把这个叫法补进去；没记过就插在上一张经过的卡片后面。然后按顺序重写 md
    public void Record(string key, string url, string title)
    {
        var clean = title.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        int existing = url.Length > 0 ? Entries.FindIndex(e => e.Url == url) : -1;
        if (existing >= 0)
        {
            if (key.Length > 0 && !Entries[existing].Keys.Contains(key)) Entries[existing].Keys.Add(key);
            if (Entries[existing].Title.Length == 0 || Core.IsPlaceholder(Entries[existing].Title)) Entries[existing].Title = clean;
            Cursor = existing;
        }
        else
        {
            int at = Math.Min((Cursor ?? Entries.Count - 1) + 1, Entries.Count);
            Entries.Insert(at, new Entry { Keys = key.Length > 0 ? [key] : [], Url = url, Title = clean });
            Cursor = at;
        }
        if (url.Length > 0) Urls.Add(url);
        Save();
    }

    public void Save()
    {
        WriteAtomic(StatePath, string.Join("\n", Entries.Select(e => $"{string.Join("|", e.Keys)}\t{e.Url}\t{e.Title}")));
        var written = new HashSet<string>();
        var lines = MdOnly.Concat(Entries.Select(e => e.Url)).Where(u => u.Length > 0 && written.Add(u))
            .Select(u => AngleStyle ? $"<{u}>  " : u);
        WriteAtomic(MdPath, string.Join("\n", lines) + "\n");
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
