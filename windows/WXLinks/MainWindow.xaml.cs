using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WXLinks.Engine;
using Forms = System.Windows.Forms;

namespace WXLinks;

public partial class MainWindow : Window
{
    const string Homepage = "https://github.com/carey314/wechat-article-links";

    string dataDir = Store.LoadDataDir();
    readonly List<LogLine> logs = [];
    Process? engine;
    string runId = "";
    DateTime startedAt;
    EngineStatus? lastRun;
    bool stopping, logsFromLastRun;
    string mode = "all";
    string? currentFile;
    List<Article> allArticles = [];
    bool ocrOK, wechatOK;
    readonly DispatcherTimer envTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    Forms.NotifyIcon? tray;

    Brush B(string key) => (Brush)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(dataDir);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/app.ico"));
        ocrOK = Platform.InitOcr();
        RefreshEnvironment();
        RefreshNav();
        LoadPreviousResult();
        UpdateMode();
        UpdateHome();
        envTimer.Tick += (_, _) => { RefreshEnvironment(); if (engine == null) RefreshNav(); };
        envTimer.Start();
        statusTimer.Tick += (_, _) => ReadStatus();
        toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; toastTimer.Stop(); };
        Closing += (_, _) => { StopAndWait(); tray?.Dispose(); };

        // 调试和截图用：--dir 指定数据文件夹，--page setup|first-account 打开时直接显示某一页
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--dir");
        if (i > 0 && i + 1 < args.Length) { dataDir = args[i + 1]; RefreshNav(); LoadPreviousResult(); UpdateHome(); }
        int p = Array.IndexOf(args, "--page");
        string page = p > 0 && p + 1 < args.Length ? args[p + 1] : "home";
        Loaded += (_, _) => Select(page);
    }

    // MARK: 侧栏

    void RefreshNav()
    {
        var keep = (Nav.SelectedItem as NavItem) is { } sel ? (sel.Kind, sel.File) : ("home", null);
        var items = new List<NavItem>
        {
            new() { Kind = "home", Glyph = "", Title = "采集", Badge = engine != null ? "采集中" : "" },
            new() { Kind = "setup", Glyph = "", Title = "设置与检查", Badge = ocrOK ? "" : "缺中文识别" },
        };
        foreach (var a in Store.Accounts(dataDir))
            items.Add(new NavItem { Kind = "account", File = a.File, Glyph = "", Title = a.Name, Badge = a.Count.ToString() });
        var same = Nav.Items.Count == items.Count && Nav.Items.Cast<NavItem>().Zip(items).All(z => z.First.Title == z.Second.Title && z.First.Badge == z.Second.Badge);
        if (same) return;
        Nav.SelectionChanged -= OnNavChanged;
        Nav.Items.Clear();
        foreach (var it in items) Nav.Items.Add(it);
        Nav.SelectedItem = items.FirstOrDefault(x => x.Kind == keep.Item1 && x.File == keep.Item2) ?? items[0];
        Nav.SelectionChanged += OnNavChanged;
        PaintNav();
    }

    /// 选中项白字，其他深色字
    void PaintNav()
    {
        foreach (NavItem it in Nav.Items)
        {
            bool on = Nav.SelectedItem == it;
            it.Fore = on ? Brushes.White : B("Ink");
            it.BadgeFore = on ? Brushes.White : (it.Kind == "setup" ? B("Orange") : B("Muted"));
        }
        Nav.Items.Refresh();
    }

    void Select(string page)
    {
        var target = page == "first-account"
            ? Nav.Items.Cast<NavItem>().FirstOrDefault(x => x.Kind == "account")
            : Nav.Items.Cast<NavItem>().FirstOrDefault(x => x.Kind == page);
        if (target != null) Nav.SelectedItem = target;
    }

    void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not NavItem it) return;
        PaintNav();
        HomePage.Visibility = it.Kind == "home" ? Visibility.Visible : Visibility.Collapsed;
        SetupPage.Visibility = it.Kind == "setup" ? Visibility.Visible : Visibility.Collapsed;
        AccountPage.Visibility = it.Kind == "account" ? Visibility.Visible : Visibility.Collapsed;
        if (it.Kind == "setup") UpdateSetup();
        if (it.Kind == "account" && it.File != null) ShowAccount(it.File);
    }

    // MARK: 环境检查

    void RefreshEnvironment()
    {
        bool w = Platform.WeChatRunning();
        if (w != wechatOK) { wechatOK = w; UpdateHome(); if (SetupPage.Visibility == Visibility.Visible) UpdateSetup(); }
    }

    // MARK: 采集页

    void OnModeChanged(object sender, RoutedEventArgs e) { mode = ModeTrial.IsChecked == true ? "trial" : ModeRecheck.IsChecked == true ? "recheck" : "all"; UpdateMode(); }

    void UpdateMode()
    {
        if (ModeHint == null) return;
        ModeHint.Text = mode switch
        {
            "trial" => "只新采 10 篇，先看看效果。再点开始会接着往下采。",
            "recheck" => "从主页顶上扫一遍，只补漏掉的，已经有的不会重复。",
            _ => "从上次停下的地方接着采，采到底后自动从头再扫一遍，把漏掉的补回原位。",
        };
    }

    UIElement Chip(bool ok, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
        sp.Children.Add(new TextBlock { FontFamily = (FontFamily)FindResource("Icons"), Text = ok ? "" : "", Foreground = ok ? B("Green") : B("Orange"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, Foreground = ok ? B("Muted") : B("Orange"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    string? noticeAction;

    void UpdateHome()
    {
        bool running = engine != null;
        ReadyCard.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RunningCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !running && ocrOK && wechatOK;
        Conditions.Children.Clear();
        Conditions.Children.Add(Chip(ocrOK, ocrOK ? "中文文字识别" : "缺中文文字识别"));
        Conditions.Children.Add(Chip(wechatOK, wechatOK ? "微信已打开" : "微信没打开"));
        noticeAction = !ocrOK ? "ocr" : !wechatOK ? "wechat" : null;
        Notice.Visibility = noticeAction == null ? Visibility.Collapsed : Visibility.Visible;
        if (noticeAction == "ocr") { NoticeText.Text = "这台电脑还没有中文文字识别，程序认不出微信里的字。到 Windows 设置里添加「中文(简体)」语言就有了。"; NoticeButton.Content = "去添加"; }
        if (noticeAction == "wechat") { NoticeText.Text = "微信没有打开。先打开微信，进入要采的公众号主页。"; NoticeButton.Content = "打开微信"; }

        ResultCard.Visibility = !running && lastRun != null ? Visibility.Visible : Visibility.Collapsed;
        if (lastRun != null) ShowResult(lastRun);
        LogCard.Visibility = logs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LogTitle.Text = running ? "运行记录" : logsFromLastRun ? "上次的运行记录" : "这次的运行记录";
        LogCount.Text = $"{logs.Count} 条";
    }

    void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        if (noticeAction == "ocr") OnInstallOcr(sender, e);
        if (noticeAction == "wechat") OnOpenWeChat(sender, e);
    }

    void OnGoSetup(object sender, RoutedEventArgs e) => Select("setup");

    void ShowResult(EngineStatus r)
    {
        string who = r.Account.Length > 0 ? r.Account + " " : "";
        (string glyph, Brush brush, string title, string detail) look = r.State switch
        {
            "finished" => ("", B("Green"), r.Added > 0 ? "采完了" : "没有新文章",
                $"{who}共 {r.Total} 条，这次新增 {r.Added} 条" + (r.Recovered > 0 ? $"，补回 {r.Recovered} 条" : "") + "。"
                + (r.Unreadable > 0 ? $"有 {r.Unreadable} 张卡片认不出标题，可以看看运行记录里的提醒。" : "")),
            "stopped" => ("", B("Muted"), "已停止", $"{who}已采 {r.Total} 条。再点开始会从停下的地方接着采。"),
            "needs_permission" => ("", B("Orange"), "还差中文文字识别", r.Reason.Length > 0 ? r.Reason : r.Message),
            _ => ("", B("Orange"), "没能完成", (r.Reason.Length > 0 ? FirstLine(r.Reason) : r.Message) + " 诊断截图存在数据文件夹的「诊断」里。"),
        };
        ResultIcon.Text = look.glyph; ResultIcon.Foreground = look.brush;
        ResultTitle.Text = look.title; ResultDetail.Text = look.detail;
        bool hasFile = r.File != null && File.Exists(Path.Combine(dataDir, r.File));
        ResultList.Visibility = ResultOpen.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
    }

    static string FirstLine(string s) => s.Split('\n')[0].Trim();

    void OnResultList(object sender, RoutedEventArgs e)
    {
        if (lastRun?.File is string f) Nav.SelectedItem = Nav.Items.Cast<NavItem>().FirstOrDefault(x => x.File == f);
    }

    void OnResultOpen(object sender, RoutedEventArgs e) { if (lastRun?.File is string f) Store.Open(Path.Combine(dataDir, f)); }

    void OnDismissResult(object sender, RoutedEventArgs e) { lastRun = null; UpdateHome(); }

    void LoadPreviousResult()
    {
        lastRun = null; logs.Clear(); logsFromLastRun = false;
        var s = EngineStatus.Read(Path.Combine(dataDir, "status.json"));
        if (s == null || s.IsActive || s.State.Length == 0) { RenderLogs(); return; }
        lastRun = s;
        var logFile = Path.Combine(dataDir, "运行日志.txt");
        if (File.Exists(logFile))
        {
            var lines = File.ReadAllLines(logFile);
            int start = Array.FindLastIndex(lines, l => l.Contains("等你松开鼠标键盘"));
            if (start >= 0)
            {
                foreach (var l in lines.Skip(start).TakeLast(400)) logs.Add(LogLine.Parse(l, B("Green"), B("Orange"), B("Muted"), B("Ink")));
                logsFromLastRun = true;
            }
        }
        RenderLogs();
    }

    void RenderLogs()
    {
        LogList.ItemsSource = null;
        LogList.ItemsSource = logs;
        LogScroll.ScrollToEnd();
    }

    // MARK: 运行引擎

    void OnStart(object sender, RoutedEventArgs e)
    {
        if (engine != null || !ocrOK || !wechatOK) return;
        runId = Guid.NewGuid().ToString();
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--engine", "--dir", dataDir, "--run-id", runId }) psi.ArgumentList.Add(a);
        if (mode == "trial") { psi.ArgumentList.Add("--max"); psi.ArgumentList.Add("10"); }
        if (mode == "recheck") psi.ArgumentList.Add("--recheck");
        try { File.Delete(Path.Combine(dataDir, ".stop")); } catch { }
        logs.Clear(); logsFromLastRun = false; lastRun = null; stopping = false;
        RenderLogs();
        try { engine = Process.Start(psi); }
        catch (Exception ex) { lastRun = new EngineStatus { State = "error", Reason = "启动失败：" + ex.Message }; UpdateHome(); return; }
        if (engine == null) return;
        engine.EnableRaisingEvents = true;
        engine.OutputDataReceived += (_, ev) => { if (ev.Data is string d && d.Trim().Length > 0) Dispatcher.BeginInvoke(() => AddLog(d)); };
        engine.ErrorDataReceived += (_, ev) => { if (ev.Data is string d && d.Trim().Length > 0) Dispatcher.BeginInvoke(() => AddLog(d)); };
        engine.Exited += (_, _) => Dispatcher.BeginInvoke(EngineExited);
        engine.BeginOutputReadLine();
        engine.BeginErrorReadLine();
        startedAt = DateTime.Now;
        StateText.Text = "正在启动"; BigNumber.Text = "0"; RunSubtitle.Text = "正在找公众号主页…"; CurrentLine.Text = "";
        StatAdded.Text = "0"; StatAvg.Text = "—"; StatRecovered.Text = "0";
        StopButton.IsEnabled = true;
        ShowTray();
        statusTimer.Start();
        RefreshNav();
        UpdateHome();
    }

    void AddLog(string line)
    {
        logs.Add(LogLine.Parse(line, B("Green"), B("Orange"), B("Muted"), B("Ink")));
        if (logs.Count > 800) logs.RemoveRange(0, logs.Count - 800);
        RenderLogs();
        LogCard.Visibility = Visibility.Visible;
        LogCount.Text = $"{logs.Count} 条";
    }

    /// 停止：给引擎留一个「.stop」文件，它在下一步停下并把剪贴板还原；10 秒还没停就强制结束
    void OnStop(object sender, RoutedEventArgs e) => RequestStop();

    void RequestStop()
    {
        if (engine == null || stopping) return;
        stopping = true;
        StopButton.IsEnabled = false;
        StateText.Text = "正在停止";
        try { File.WriteAllText(Path.Combine(dataDir, ".stop"), "stop"); } catch { }
        var p = engine;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        t.Tick += (_, _) => { t.Stop(); try { if (!p.HasExited) p.Kill(); } catch { } };
        t.Start();
    }

    void StopAndWait()
    {
        if (engine == null) return;
        try { File.WriteAllText(Path.Combine(dataDir, ".stop"), "stop"); } catch { }
        try { if (!engine.WaitForExit(4000)) engine.Kill(); } catch { }
    }

    void ReadStatus()
    {
        var s = EngineStatus.Read(Path.Combine(dataDir, "status.json"));
        if (s == null || s.RunId != runId) return;
        Elapsed.Text = "已用 " + Fmt(DateTime.Now - startedAt);
        BigNumber.Text = s.Total.ToString();
        StatAdded.Text = s.Added.ToString();
        StatAvg.Text = s.Avg > 0 ? $"{s.Avg:F1} 秒" : "—";
        StatRecovered.Text = s.Recovered.ToString();
        RunSubtitle.Text = s.Account.Length == 0 ? "正在找公众号主页…" : $"{s.Account} · " + (s.Phase == "recheck" ? "补漏中（从头再扫一遍）" : "第一遍");
        CurrentLine.Text = s.LastTitle.Length > 0 ? "刚采到：" + s.LastTitle : s.Message;
        if (!stopping)
        {
            (string text, string dot, string fg, string bg) = s.State switch
            {
                "running" => ("采集中", "Green", "GreenDeep", "GreenSoft"),
                "paused" => ("已暂停", "Orange", "Orange", "OrangeSoft"),
                "waiting_idle" => ("准备开始", "Green", "GreenDeep", "GreenSoft"),
                _ => ("正在启动", "Muted", "Muted", "Row"),
            };
            StateText.Text = text; StateDot.Fill = B(dot); StateText.Foreground = B(fg); StatePill.Background = B(bg);
        }
        RunHint.Text = s.State switch
        {
            "waiting_idle" => "请松开鼠标和键盘，5 秒后开始替你操作微信。",
            "paused" => "你碰了鼠标或切走了微信，已暂停。停手 5 秒后自动继续。",
            _ => "正在替你操作微信，请别碰鼠标。鼠标甩到屏幕左上角立即停止。",
        };
        if (tray != null) tray.Text = Trim63($"公众号链接采集 · {StateText.Text} · {s.Total} 条");
    }

    static string Trim63(string s) => s.Length > 63 ? s[..63] : s;

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    void EngineExited()
    {
        statusTimer.Stop();
        ReadStatus();
        var s = EngineStatus.Read(Path.Combine(dataDir, "status.json"));
        if (s == null || s.RunId != runId || s.IsActive)
        {
            s ??= new EngineStatus();
            s.State = "error";
            if (s.Reason.Length == 0) s.Reason = logs.LastOrDefault()?.Text ?? "程序意外退出";
        }
        lastRun = s;
        engine = null;
        stopping = false;
        HideTray();
        RefreshNav();
        UpdateHome();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        System.Media.SystemSounds.Asterisk.Play();
    }

    // 采集时微信盖在窗口上面：右下角托盘图标显示进度，右键能停止
    void ShowTray()
    {
        tray ??= new Forms.NotifyIcon();
        try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        tray.Text = "公众号链接采集 · 正在启动";
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("停止采集", null, (_, _) => Dispatcher.BeginInvoke(RequestStop));
        menu.Items.Add("打开主窗口", null, (_, _) => Dispatcher.BeginInvoke(() => { WindowState = WindowState.Normal; Activate(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => { WindowState = WindowState.Normal; Activate(); });
        tray.Visible = true;
    }

    void HideTray() { if (tray != null) tray.Visible = false; }

    // MARK: 设置与检查

    void UpdateSetup()
    {
        OcrIcon.Text = ocrOK ? "" : ""; OcrIcon.Foreground = ocrOK ? B("Green") : B("Orange");
        OcrTitle.Text = ocrOK ? "中文文字识别：已就绪" : "中文文字识别：还没有";
        OcrDetail.Text = ocrOK
            ? $"用的是 Windows 自带的识别（{Platform.OcrLanguage}），全程在本机完成，不联网。"
            : "程序靠它认出微信里的标题和按钮。打开 设置 → 时间和语言 → 语言和区域 →「添加语言」，选「中文(简体，中国)」，装好后回到这里点「重新检测」。";
        OcrButton.Content = ocrOK ? "重新检测" : "去添加中文";
        WeChatIcon.Text = wechatOK ? "" : ""; WeChatIcon.Foreground = wechatOK ? B("Green") : B("Orange");
        WeChatDetail.Text = wechatOK ? "已打开。采集前进入要采的公众号主页。" : "没有打开。";
        WeChatButton.Visibility = wechatOK ? Visibility.Collapsed : Visibility.Visible;
        DataDirText.Text = dataDir;
        ChangeDirButton.IsEnabled = engine == null;
        bool sc = File.Exists(Installer.StartMenuShortcut);
        ShortcutIcon.Text = sc ? "" : ""; ShortcutIcon.Foreground = sc ? B("Green") : B("Muted");
        ShortcutTitle.Text = Installer.IsInstalledCopy ? (sc ? "已装好，开始菜单里有「公众号链接采集」" : "已装好，开始菜单里还没有") : "没有装（正在直接运行下载的文件）";
        ShortcutDetail.Text = Installer.IsInstalledCopy
            ? $"装在 {Installer.Dir}。下载新版本后打开一次，就会替换旧的。不想用了，删掉这个文件夹和开始菜单里的快捷方式就行。"
            : $"正在运行：{Environment.ProcessPath}。";
        ShortcutButton.Content = File.Exists(Installer.DesktopShortcut) ? "重新添加快捷方式" : "桌面也放一个快捷方式";
        VersionText.Text = $"版本 {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · 开源项目，MIT 协议。新版本、问题反馈都在项目主页。";
    }

    void OnInstallOcr(object sender, RoutedEventArgs e)
    {
        if (!ocrOK) Store.Open("ms-settings:regionlanguage");
        ocrOK = Platform.InitOcr();
        UpdateSetup(); UpdateHome(); RefreshNav();
        Flash(ocrOK ? "中文文字识别已就绪" : "还没有中文文字识别");
    }

    void OnOpenWeChat(object sender, RoutedEventArgs e)
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "Weixin", "Weixin.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "WeChat", "WeChat.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat", "WeChat.exe"),
        })
            if (File.Exists(path)) { Store.Open(path); return; }
        Flash("没找到微信的安装位置，请自己打开微信");
    }

    void OnProbe(object sender, RoutedEventArgs e)
    {
        if (engine != null) return;
        ProbeButton.IsEnabled = false;
        ProbeOutput.Visibility = Visibility.Visible;
        ProbeOutput.Text = "正在检查…（会把微信切到前面看一眼，不会点任何东西）";
        var dir = dataDir;
        Task.Run(() =>
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "--engine", "--probe", "--dir", dir }) psi.ArgumentList.Add(a);
            string text;
            try { using var p = Process.Start(psi)!; text = p.StandardOutput.ReadToEnd(); p.WaitForExit(60000); }
            catch (Exception ex) { text = "检查失败：" + ex.Message; }
            Dispatcher.BeginInvoke(() => { ProbeOutput.Text = text.Trim(); ProbeButton.IsEnabled = true; Activate(); });
        });
    }

    void OnChangeDir(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "选一个文件夹，存放采到的链接", SelectedPath = dataDir, UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        dataDir = dlg.SelectedPath;
        Store.SaveDataDir(dataDir);
        LoadPreviousResult();
        RefreshNav(); UpdateSetup(); UpdateHome();
    }

    void OnOpenDataFolder(object sender, RoutedEventArgs e) { Directory.CreateDirectory(dataDir); Store.Open(dataDir); }

    void OnOpenDiag(object sender, RoutedEventArgs e)
    {
        var d = Path.Combine(dataDir, "诊断");
        Directory.CreateDirectory(d);
        Store.Open(d);
    }

    void OnShortcuts(object sender, RoutedEventArgs e)
    {
        try
        {
            Installer.MakeShortcut(Installer.StartMenuShortcut);
            Installer.MakeShortcut(Installer.DesktopShortcut);
            Flash("已添加到开始菜单和桌面");
        }
        catch (Exception ex) { Flash("添加失败：" + ex.Message); }
        UpdateSetup();
    }

    void OnHomepage(object sender, RoutedEventArgs e) => Store.Open(Homepage);

    // MARK: 链接列表

    void ShowAccount(string file)
    {
        currentFile = file;
        allArticles = Store.Articles(dataDir, file);
        var acc = Store.Accounts(dataDir).FirstOrDefault(a => a.File == file);
        AccountName.Text = acc?.Name ?? Path.GetFileNameWithoutExtension(file);
        AccountSummary.Text = $"{allArticles.Count} 篇 · 从新到旧" + (acc != null ? $" · {acc.Modified:M月d日 HH:mm} 更新" : "");
        Search.Text = "";
        ApplySearch();
    }

    void OnSearch(object sender, TextChangedEventArgs e) => ApplySearch();

    void ApplySearch()
    {
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = q.Length == 0 ? allArticles : allArticles.Where(a => a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Articles.ItemsSource = shown;
        EmptyList.Text = q.Length == 0 ? "还没有链接" : $"没有找到「{q}」";
        EmptyList.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    List<Article> Selected() => Articles.SelectedItems.Cast<Article>().OrderBy(a => a.Index).ToList();

    void Copy(string text, string note)
    {
        try { Clipboard.SetText(text); Flash(note); } catch { Flash("复制失败，剪贴板被别的程序占着"); }
    }

    void OnCopyAll(object sender, RoutedEventArgs e) => Copy(string.Join("\r\n", allArticles.Select(a => a.Url)), $"已复制 {allArticles.Count} 条链接");
    void OnCopySelected(object sender, RoutedEventArgs e) { var s = Selected(); if (s.Count > 0) Copy(string.Join("\r\n", s.Select(a => a.Url)), $"已复制 {s.Count} 条链接"); }
    void OnCopyMarkdown(object sender, RoutedEventArgs e) { var s = Selected(); if (s.Count > 0) Copy(string.Join("\r\n", s.Select(a => $"[{a.Title}]({a.Url})")), "已复制"); }
    void OnOpenSelected(object sender, RoutedEventArgs e) { foreach (var a in Selected().Take(20)) Store.Open(a.Url); }
    void OnArticleDoubleClick(object sender, MouseButtonEventArgs e) { if (Articles.SelectedItem is Article a) Store.Open(a.Url); }
    void OnArticlesKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { OnCopySelected(sender, e); e.Handled = true; }
        if (e.Key == Key.Enter) { OnOpenSelected(sender, e); e.Handled = true; }
    }
    void OnOpenFile(object sender, RoutedEventArgs e) { if (currentFile != null) Store.Open(Path.Combine(dataDir, currentFile)); }
    void OnRevealFile(object sender, RoutedEventArgs e) { if (currentFile != null) Store.Reveal(Path.Combine(dataDir, currentFile)); }

    void Flash(string s)
    {
        ToastText.Text = s;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop(); toastTimer.Start();
    }
}
