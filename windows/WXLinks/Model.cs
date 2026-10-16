using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using WXLinks.Engine;

namespace WXLinks;

/// 引擎写在 status.json 里的进度
public class EngineStatus
{
    public string State = "", Message = "", Account = "", MdPath = "", Phase = "", LastTitle = "", Reason = "", RunId = "";
    public int Total, Added, Recovered, Reopened, Unreadable;
    public double Avg;

    public bool IsActive => State is "starting" or "waiting_idle" or "running" or "paused";
    public string? File => MdPath.Length == 0 ? null : Path.GetFileName(MdPath);

    public static EngineStatus? Read(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            double D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            return new EngineStatus
            {
                State = S("state"), Message = S("message"), Account = S("account"), MdPath = S("md_path"), Phase = S("phase"),
                LastTitle = S("last_title"), Reason = S("reason"), RunId = S("run_id"),
                Total = I("total_links"), Added = I("added_this_run"), Recovered = I("recovered"), Reopened = I("reopened"),
                Unreadable = I("unreadable"), Avg = D("avg_seconds"),
            };
        }
        catch { return null; }
    }
}

public record Account(string File, string Name, int Count, DateTime Modified);

public record Article(int Index, string Title, string Url)
{
    public string ShortUrl => Url.Replace("https://", "");
}

/// 侧栏的一项
public class NavItem
{
    public string Kind = "";       // home / setup / account
    public string? File;
    public string Glyph { get; set; } = "";
    public string Title { get; set; } = "";
    public string Badge { get; set; } = "";
    public Brush Fore { get; set; } = Brushes.Black;
    public Brush BadgeFore { get; set; } = Brushes.Gray;
}

/// 运行记录的一行
public class LogLine
{
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Glyph { get; set; } = "";
    public Brush GlyphBrush { get; set; } = Brushes.Gray;
    public Brush TextBrush { get; set; } = Brushes.Black;

    static readonly Regex Collected = new(@"^(✅|🔁 补漏) 第 (\d+) 条（([\d.]+) 秒）：(.*?)\s+(https?://\S+)$");

    public static LogLine Parse(string raw, Brush green, Brush orange, Brush muted, Brush ink)
    {
        var line = raw.Trim();
        var time = "";
        if (line.StartsWith('[') && line.IndexOf(']') is int close and > 0) { time = line[1..close]; line = line[(close + 1)..].Trim(); }
        var m = Collected.Match(line);
        if (m.Success)
        {
            bool rec = m.Groups[1].Value.StartsWith("🔁");
            return new LogLine { Time = time, Text = $"第 {m.Groups[2].Value} 条 · {m.Groups[4].Value}", Detail = $"{m.Groups[3].Value} 秒",
                Glyph = rec ? "" : "", GlyphBrush = green, TextBrush = ink };
        }
        foreach (var (prefix, glyph, brush) in new[] { ("⚠️", "", orange), ("⏸", "", orange), ("▶️", "", green) })
            if (line.StartsWith(prefix))
                return new LogLine { Time = time, Text = line[prefix.Length..].Trim(), Glyph = glyph, GlyphBrush = brush, TextBrush = ink };
        return new LogLine { Time = time, Text = line, Glyph = "", GlyphBrush = muted, TextBrush = muted };
    }
}

/// 数据文件夹、采过的公众号、链接列表
public static class Store
{
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WXLinks", "settings.json");

    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "公众号链接采集");

    public static string LoadDataDir()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("dataDir", out var v) && v.GetString() is string s && s.Length > 0) return s;
        }
        catch { }
        return DefaultDataDir;
    }

    public static void SaveDataDir(string dir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { dataDir = dir }), new UTF8Encoding(false));
    }

    public static List<Account> Accounts(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.md").Where(f => !Path.GetFileName(f).StartsWith('.'))
            .Select(f =>
            {
                int n = 0;
                try { n = File.ReadAllLines(f).Count(l => Core.ExtractUrl(l) != null); } catch { }
                return new Account(Path.GetFileName(f), Path.GetFileNameWithoutExtension(f), n, File.GetLastWriteTime(f));
            })
            .OrderByDescending(a => a.Modified).ToList();
    }

    /// md 里的链接按顺序列出来，标题从进度记录里找
    public static List<Article> Articles(string dir, string file)
    {
        var md = Path.Combine(dir, file);
        if (!File.Exists(md)) return [];
        var urls = File.ReadAllLines(md).Select(Core.ExtractUrl).Where(u => u != null).Select(u => u!).ToList();
        var titles = new Dictionary<string, string>();
        var state = md + ".已点过.txt";
        if (File.Exists(state))
            foreach (var l in File.ReadAllLines(state))
            {
                var f = l.Split('\t', 3);
                if (f.Length > 2 && f[1].Length > 0 && !Core.IsPlaceholder(f[2])) titles[f[1]] = f[2];
            }
        return urls.Select((u, i) => new Article(i + 1, titles.TryGetValue(u, out var t) ? t : "（没记下标题）", u)).ToList();
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    public static void Reveal(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }
}
