using System.Text;
using WXLinks.Engine;
using static WXLinks.Engine.Core;

// 逻辑测试（不碰系统）：dotnet run --project Tests [-- 样本目录]
// 1) 卡片识别：每个样本按 Mac 版 --test-cards 同样的格式打印，和 expected 目录里 Mac 版跑出来的结果逐行比对
// 2) 「加载中」的认法、记录文件合并重复、插回原位
Console.OutputEncoding = Encoding.UTF8;
var dir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fixtures");
int fail = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "通过  " : "失败  ") + what); if (!ok) fail++; }
static string R(double v) => ((int)Math.Round(v, MidpointRounding.AwayFromZero)).ToString();

foreach (var f in Directory.GetFiles(dir, "*.txt").OrderBy(x => x))
{
    var boxes = new List<TextBox>();
    foreach (var l in File.ReadAllLines(f))
    {
        var p = l.Split('\t');
        if (p[0] == "WIN") { WinW = double.Parse(p[1]); WinH = double.Parse(p[2]); continue; }
        if (p.Length >= 5 && double.TryParse(p[0], out var x)) boxes.Add(new TextBox(string.Join("\t", p[4..]), new Box(x, double.Parse(p[1]), double.Parse(p[2]), double.Parse(p[3]))));
    }
    Scale = 1;
    var o = new List<string> { $"PROFILE\t{(IsProfilePage(boxes) ? "是" : "否")}" };
    var (cards, notRead) = VisibleCards(boxes, 0);
    o.AddRange(cards.Select(c => $"CARD\t{c.Key}\t{c.Title}\t{R(c.X)}\t{R(c.Y)}"));
    o.AddRange(notRead.Select(u => $"UNREAD\t{u.Split('，')[0]}"));
    var (top, bottom) = ClickableRange(boxes, 0);
    var plan = PlannedScroll(boxes, 0);
    o.Add($"RANGE\t{R(top)}\t{R(bottom)}");
    o.Add($"PLAN\t{R(plan.Points)}\t{plan.Anchor?.Text ?? ""}");
    var expPath = Path.Combine(Path.GetDirectoryName(f)!, "expected", Path.GetFileName(f));
    var exp = File.Exists(expPath) ? File.ReadAllLines(expPath).Where(l => l.Length > 0).ToList() : [];
    bool same = exp.SequenceEqual(o);
    Check(same, $"卡片识别和 Mac 版一致：{Path.GetFileName(f)}");
    if (!same)
    {
        Console.WriteLine("  Mac 版：\n    " + string.Join("\n    ", exp));
        Console.WriteLine("  Windows 版：\n    " + string.Join("\n    ", o));
    }
}

foreach (var t in new[] { "正在加载", "正在加毂", "正在加穀", "）彳微信公众平台", "1微信公众平台", "微信…" }) Check(IsPlaceholder(t), $"认出加载中：{t}");
foreach (var t in new[] { "聊聊DeepSeek V4正式版，真正的智能平权。", "正在发生的AI革命" }) Check(!IsPlaceholder(t), $"认出是标题：{t}");
Check(Normalize("Al让数学再也回不去") == Normalize("AI让数学再也回不去"), "I 和 l 认混了也算同一个标题");

// 记录文件：合并重复、补漏插回原位、md 格式保持
var tmp = Path.Combine(Path.GetTempPath(), "wxlinks-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
var md = Path.Combine(tmp, "测试号.md");
File.WriteAllText(md, "<https://mp.weixin.qq.com/s/A>  \n<https://mp.weixin.qq.com/s/B>  \n");
File.WriteAllText(md + ".已点过.txt", "第一篇标题abcd\thttps://mp.weixin.qq.com/s/A\t第一篇标题abcd\n正在加毂\thttps://mp.weixin.qq.com/s/B\t正在加毂\n第二篇\thttps://mp.weixin.qq.com/s/B\t第二篇真标题xyz\n");
var rec = Records.Load(md);
Check(rec.Entries.Count == 2 && rec.Urls.Count == 2, "同一个链接的两条记录合并成一条");
Check(rec.Entries[1].Title == "第二篇真标题xyz", "合并时用真标题替换「正在加毂」");
Check(rec.SeenIndex(Normalize("第二篇真标题xyz")) == 1, "真标题也算一种叫法，下次不会再点开");
Check(rec.AngleStyle, "认出 md 是「<链接>」格式");
rec.Cursor = 0;
rec.Record("补回来的一篇", "https://mp.weixin.qq.com/s/C", "补回来的一篇");
var lines = File.ReadAllLines(md);
Check(lines.Length == 3 && lines[1] == "<https://mp.weixin.qq.com/s/C>  ", "补漏补到的插回原位，md 保持原格式");
Directory.Delete(tmp, true);

// 3) 标签栏像素（可选，样本不进仓库：是 Windows 微信录屏里截的帧，有别人的文章和头像）
//    dotnet run --project Tests -- fixtures <帧目录>。目录里是 .pgm 灰度图（亮度 = (R+G+B)/3）和 expected.txt，
//    expected.txt 每行「文件名<TAB>鼠标所在列或 -<TAB>标签左<TAB>标签右<TAB>···中心<TAB>×中心」，误差 4 像素以内算对
if (args.Length > 1 && Directory.Exists(args[1]))
{
    static (byte[] L, int W, int H) ReadPgm(string path)
    {
        var all = File.ReadAllBytes(path);
        int pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)all[pos])) pos++;
            int s = pos;
            while (!char.IsWhiteSpace((char)all[pos])) pos++;
            return Encoding.ASCII.GetString(all, s, pos - s);
        }
        Token(); int w = int.Parse(Token()), h = int.Parse(Token()); Token(); pos++;
        return (all[pos..(pos + w * h)], w, h);
    }
    var expected = Path.Combine(args[1], "expected.txt");
    var exp = File.Exists(expected) ? File.ReadAllLines(expected).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToDictionary(p => p[0]) : [];
    foreach (var f in Directory.GetFiles(args[1], "*.pgm").OrderBy(x => x))
    {
        var name = Path.GetFileName(f);
        var (lum, w, h) = ReadPgm(f);
        double? avoid = exp.TryGetValue(name, out var e) && e[1] != "-" ? double.Parse(e[1]) : null;
        var tab = Vision.ActiveTab(lum, w, h, 1, avoid);
        string got = tab is Box t ? $"标签 {t.X:F0}-{t.Right:F0}（高 {t.H:F0}）" : "没认出标签";
        if (tab is Box tb)
        {
            var b = Vision.TabButtons(lum, w, h, tb);
            got += $" ··· {b.Dots:F0} × {b.Close:F0}{(b.Found ? "" : "（推算）")}";
            if (e != null)
            {
                bool ok = Math.Abs(tb.X - double.Parse(e[2])) <= 4 && Math.Abs(tb.Right - double.Parse(e[3])) <= 4
                    && Math.Abs(b.Dots - double.Parse(e[4])) <= 4 && Math.Abs(b.Close - double.Parse(e[5])) <= 4;
                Check(ok, $"录屏帧 {name}：{got}");
                continue;
            }
        }
        else if (e != null) { Check(false, $"录屏帧 {name}：{got}"); continue; }
        Console.WriteLine($"      {name}：{got}");
    }
}

Console.WriteLine(fail == 0 ? "全部通过" : $"{fail} 项失败");
return fail == 0 ? 0 : 1;
