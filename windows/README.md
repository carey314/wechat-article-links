# 公众号链接采集 · Windows 版（测试版）

状态（2026-10-16）：**测试版，从 v1.1.0 起随 Mac 版一起发布**。按 Windows 微信 4.x 的操作录屏适配，程序还没在真实的 Windows 微信上跑过一整轮，等第一批实测反馈。

## 适配的界面（2026-10 录屏）

- 公众号主页和文章都开在同一个「微信」浏览器窗口的标签里；点卡片后文章在主页标签**右边**新开一个标签
- 新标签先显示绿色微信图标和「微信公众平台」，加载出来才换成文章标题
- 当前标签是一整块浅灰（亮度约 235），标签栏和其他标签接近白（约 250）；**鼠标停在哪个标签上它也会变灰**，所以程序看标签前先把鼠标挪开
- 当前标签右端：「···」和「×」；加载中「···」左边多一个「⊗」，悬停时多一个「↻」
- 「···」菜单向左下展开：问 AI / 刷新 / **复制链接** / 调整文字大小 / … / 关闭全部标签页（Ctrl+Shift+W）。程序只点认出来是「复制链接」的那一项
- 点「复制链接」后页面中间弹「已复制」；点文章标签的「×」回到主页标签，列表停在原位

## 各部分

| 部分 | 文件 | 测过什么 |
|---|---|---|
| 核心规则：认卡片、算翻页、比标题、「加载中」的认法、记录文件（格式和 Mac 版相同，两边的数据文件可以互用） | `WXLinks/Engine/Core.cs`、`Shapes.cs` | 和 Mac 版对照测试（样本在 `Tests/fixtures/`，标准答案由 Mac 版 `wxlinks-engine --test-cards` 生成） |
| 认标签栏：当前标签、「···」、「×」 | `WXLinks/Engine/Vision.cs` | 用录屏帧测过（帧不进仓库，见下） |
| 采集流程：点开 →「···」→ 复制链接 →「×」，翻页、补漏 | `WXLinks/Engine/Collector.cs` | 只编译通过，没在 Windows 微信上跑过 |
| 系统调用：找微信窗口、截屏、Windows 自带中文识别、鼠标键盘、剪贴板 | `WXLinks/Engine/Platform.cs` | 云端 Windows 上跑「只看不点」检查和识别自检 |
| 界面（WPF）：采集页、设置与检查、链接列表、托盘图标 | `WXLinks/App.xaml`、`MainWindow.xaml(.cs)`、`Model.cs` | 云端 Windows 上打开并截图 |
| 第一次打开自动安装到 `%LOCALAPPDATA%\Programs\公众号链接采集\`、建开始菜单快捷方式 | `WXLinks/Installer.cs` | 云端 Windows 上测过 |

点「···」（v1.1.1 起）：按「上次点中的位置 → 认出的「···」→ 标签右半边的小图标从右往左（最右边那个多半是「×」，放最后）→ 按录屏推算的位置」挨个试，每点一下都认一遍有没有弹出菜单（认出两个以上菜单项才算），没弹出就按 Esc 换下一个；点中了记住它离标签右边几个标签高，后面的文章直接点那里。

关文章（v1.1.1 起）：先确认当前标签就是「主页标签右边、刚打开的那篇文章」、微信浏览器窗口在最前面，再按 Ctrl+W（只关当前标签）；按了没回到主页就改点「×」，可能的位置挨个试，每次点前再确认一遍。回没回到主页看两样：当前标签回到左边，或者页面顶上认出「全部 / 文章」分栏。万一 Ctrl+W 把整个窗口关了，会在数据文件夹留一个「.不按CtrlW」，以后只点「×」。认不准的一律不关，存诊断截图后停下。

诊断截图（数据文件夹的「诊断」里）：每次运行第一篇文章打开后存一张；点不开菜单、关不掉文章时也存。截图上红框是认出的当前标签，蓝线是「···」「×」可能在的位置，绿十字加序号是实际点过的位置；另有一张「-标签栏放大.png」。

## 编译和测试（Mac 上也行）

需要 .NET 10 SDK（官方下载 https://dotnet.microsoft.com/download ）。

```
dotnet run --project Tests                         # 规则对照测试
dotnet run --project Tests -- Tests/fixtures <帧目录>   # 再加上用录屏帧测认标签栏（帧目录见下）
dotnet build WXLinks/WXLinks.csproj                # 编译（Mac 上能编，不能运行）
dotnet publish WXLinks/WXLinks.csproj -c Release   # 在 Windows 上出单文件 exe（自带运行库）
```

帧目录：从 Windows 微信录屏里抽帧，存成 `.pgm` 灰度图（亮度取 (R+G+B)/3，只要顶上 100 行），旁边放 `expected.txt`，每行「文件名、鼠标所在列或 -、标签左、标签右、···中心、×中心」，用 TAB 分开。帧里有别人的文章和头像，不进仓库。

调试参数（带 `--engine` 启动是引擎）：

```
WXLinks.exe --engine --probe [--dir 数据文件夹]      # 只看不点，打印认到的标签、卡片，并存一张诊断截图
WXLinks.exe --engine --ocr-test                      # 画几行字再用系统识别认一遍
WXLinks.exe --no-install [--page setup]              # 直接打开界面，不自动安装
```

改了卡片识别规则，要同时改 Mac 版 `Sources/engine.swift`，再用 `wxlinks-engine --test-cards` 重新生成 `Tests/fixtures/expected/`。

## 发布

推 `v` 开头的 tag，`.github/workflows/release.yml` 在 windows-latest 上：跑对照测试 → 出单文件 exe（`WXLinks-windows.exe`）→ 打开界面截图 → 测自动安装 → 跑「只看不点」检查 → 装中文识别并自检（装不上不影响发布）；和 Mac 任务都成功后一起发到 Releases。
