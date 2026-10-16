namespace WXLinks.Engine;

/// 看标签栏的像素（不碰系统，Mac 上也能测）。按 Windows 微信 4.x 内置浏览器的样子（2026-10 录屏实测）：
/// - 标签栏底色接近白（亮度约 250），没选中的标签也是白的；当前标签是一整块浅灰（约 235）。
///   鼠标停在哪个标签上它也会变成同样的灰，所以看之前要把鼠标挪开，或者告诉它鼠标在哪一列
/// - 当前标签右端是「···」和「×」两个图标；加载中时「···」左边多一个「⊗ 停止」，悬停时多一个「↻ 刷新」
/// - 标签栏下面就是网页（白底 255）
/// 坐标都是这张截图里的像素
public static class Vision
{
    public static bool Light(int v) => v >= 222 && v <= 244;

    /// 标签栏有多高：从上往下，碰到一整行几乎全白（网页）就是标签栏到头了；认不出就当整张图都是
    public static int BarBottom(byte[] luma, int w, int h)
    {
        for (int y = h / 4; y < h; y++)
        {
            int white = 0;
            for (int x = 0; x < w; x++) if (luma[y * w + x] >= 253) white++;
            if (white > w * 0.6) return y;
        }
        return h;
    }

    /// 当前标签的位置：每一列数浅灰的点，浅灰点多的列连成一段、宽度像个标签的那段（Mac 版 activeTab 的 Windows 版）。
    /// avoidX：鼠标所在的列。有两段都像（一个是当前标签，一个是鼠标停着的标签）时，不选鼠标那段
    public static Box? ActiveTab(byte[] luma, int w, int h, double scale, double? avoidX = null)
    {
        int bottom = BarBottom(luma, w, h);
        if (bottom < 8) return null;
        var count = new int[w];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < bottom; y++)
                if (Light(luma[y * w + x])) count[x]++;
        int max = count.Max();
        if (max < 8 * scale) return null;
        // 标签里有字和图标的列浅灰点会少一些（左边的头像能占掉一半），标签之间的空隙几乎没有
        int thr = (int)Math.Max(5 * scale, max * 0.25);
        int gapMax = (int)Math.Max(2, 3 * scale);
        var spans = new List<(int L, int R)>();
        for (int x = 0; x < w;)
        {
            if (count[x] < thr) { x++; continue; }
            int end = x, gap = 0, k = x + 1;
            while (k < w && gap <= gapMax) { if (count[k] >= thr) { end = k; gap = 0; } else gap++; k++; }
            spans.Add((x, end));
            x = end + 1;
        }
        var tabs = spans.Where(s => s.R - s.L >= 90 * scale && s.R - s.L <= 520 * scale).ToList();
        if (tabs.Count == 0) return null;
        if (tabs.Count > 1 && avoidX is double ax)
        {
            var away = tabs.Where(s => ax < s.L - 2 || ax > s.R + 2).ToList();
            if (away.Count > 0) tabs = away;
        }
        // 还有好几段：取浅灰最整齐的那段（当前标签整块都是灰的）
        var best = tabs.MaxBy(s => Enumerable.Range(s.L, s.R - s.L + 1).Average(x => count[x]));
        // 上下边：这一段里大半列都是浅灰的那几行
        int top = -1, bot = -1;
        for (int y = 0; y < bottom; y++)
        {
            int n = 0;
            for (int x = best.L; x <= best.R; x++) if (Light(luma[y * w + x])) n++;
            if (n >= (best.R - best.L + 1) * 0.6) { if (top < 0) top = y; bot = y; }
        }
        if (top < 0 || bot - top < 16 * scale) return null;
        return new Box(best.L, top, best.R - best.L + 1, bot - top + 1);
    }

    /// 标签上的图标和字：标签灰底上一团一团的深色，从左到右，返回左右边（Mac 版 dotsCenterX 的找法）
    public static List<(double X0, double X1)> TabMarks(byte[] luma, int w, int h, Box tab)
    {
        int th = (int)tab.H;
        int bgRow = Math.Clamp((int)tab.Y + 2, 0, h - 1);   // 标签最上面一条：在图标上面，只有底色
        int y0 = (int)(tab.Y + th * 0.2), y1 = Math.Min(h, (int)(tab.Y + th * 0.85));
        int l = Math.Max(0, (int)tab.X), r = Math.Min(w - 1, (int)tab.Right);
        var cols = new bool[r - l + 1];
        for (int x = l; x <= r; x++)
        {
            if (!Light(luma[bgRow * w + x])) continue;
            for (int y = y0; y < y1 && !cols[x - l]; y++) cols[x - l] = luma[y * w + x] < 170;
        }
        var marks = new List<(double, double)>();
        int gapMax = Math.Max(2, th / 18);
        for (int i = 0; i < cols.Length;)
        {
            if (!cols[i]) { i++; continue; }
            int end = i, gap = 0, k = i + 1;
            while (k < cols.Length && gap <= gapMax) { if (cols[k]) { end = k; gap = 0; } else gap++; k++; }
            marks.Add((l + i, l + end));
            i = end + 1;
        }
        return marks;
    }

    /// 当前标签上「···」和「×」的中心 x。最右边那个图标是「×」，紧挨在它左边的是「···」；
    /// 找不到就按录屏量的位置推（「×」离右边 0.53 个标签高，「···」离右边 1.4 个标签高）。
    /// DotsLeft：「···」的左边，标题认到这儿为止
    public static (double Dots, double Close, double DotsLeft, bool Found) TabButtons(byte[] luma, int w, int h, Box tab)
    {
        double th = tab.H;
        var icons = TabMarks(luma, w, h, tab).Where(m => m.X1 - m.X0 >= th * 0.2 && m.X1 - m.X0 <= th * 0.85).ToList();
        var close = icons.LastOrDefault(m => m.X1 <= tab.Right && m.X1 >= tab.Right - th * 1.2);
        if (close != default)
        {
            double cm = (close.X0 + close.X1) / 2;
            var dots = icons.LastOrDefault(m => m.X1 < close.X0 && (m.X0 + m.X1) / 2 >= cm - th * 1.6 && (m.X0 + m.X1) / 2 <= cm - th * 0.5);
            if (dots != default) return ((dots.X0 + dots.X1) / 2, cm, dots.X0, true);
        }
        double d = tab.Right - th * 1.4;
        return (d, tab.Right - th * 0.53, d - th * 0.3, false);
    }

    /// 标签上标题那一块：跳过左边的头像 / 微信图标，到「···」左边为止（加载中时中间的「⊗」认成的乱字在后面，不影响比对开头）
    public static Box TitleArea(Box tab, double dotsLeft)
    {
        double x0 = tab.X + tab.H * 0.95;
        double x1 = Math.Max(dotsLeft - tab.H * 0.15, x0 + tab.H);
        return new Box(x0, tab.Y, x1 - x0, tab.H);
    }
}
