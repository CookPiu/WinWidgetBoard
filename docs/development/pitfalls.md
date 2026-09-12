# 已付出代价的陷阱

文档状态：已批准
版本：1.0
日期：2026-09-12

本文件只收录**已经造成过一次真实故障或一轮无谓排查**的事实。每条都写清：现象、真实原因、正确写法，
以及下次遇到同类症状时先看哪里。不收录尚未发生的假想风险，也不重复其他文档已经拥有的结论——
构建与验证流程见 [CONTRIBUTING.md](../../CONTRIBUTING.md)，工具链锁定值见 [toolchain.md](toolchain.md)，
UI 规范见 [02-ux-design-spec.md](../02-ux-design-spec.md)。

## 1. 入口的 UI 线程绝不能无界地等待 Explorer

任务栏入口把 `Shell_TrayWnd` 认作 owner（[ADR-0023](../adr/0023-embedded-taskbar-entry-strip.md)），
这是它不去争抢 topmost 波段却仍能压在条带之上的原因。代价是：建立和重申这层 owner 关系的调用会伸进
Explorer 的 UI 线程，而那些调用**自身没有超时**。Explorer 繁忙时——已观测到的触发点是servicing
广播 `WM_SETTINGCHANGE`，而这条广播本身又正是安排入口下一次重定位的东西——入口停止抽取消息，
Windows 按无响应应用把它杀掉。**所有子进程随 `KILL_ON_JOB_CLOSE` job 一起死**，于是一次卡住的
shell 会把面板和 broker 一并带走，并且**任何地方都不会留下崩溃转储：挂起不是崩溃**。

这件事已经发生过三次（2026-08-21、08-29、09-11）。第一次的修复 `e32dd22` 从 `TaskbarGeometry.cpp`
移除了 `SHAppBarMessage`——方向正确，但只覆盖了其中一个调用。`EnsureTaskbarOwner` 与 `EnsureTopmost`
每 500 ms 在同一条线程上运行，两者都会触碰跨进程的 owner 链。

**规则**：入口 UI 线程上任何可能抵达 Explorer 的动作，先过 `LauncherWindow::IsTaskbarResponsive`，
探测失败就跳过这一轮。跳过的代价是一个 500 ms 周期内 z-order 陈旧；不跳过的代价是整棵进程树。
探测必须是 `SendMessageTimeout(..., WM_NULL, SMTO_ABORTIFHUNG, 50)`，**不能用 `SMTO_BLOCK`**：
触发场景本就是 Explorer 在向我们发送消息，在等待自己的发送时拒绝处理对方的发送，正是两个健康进程
互相死锁的方式。

**下次怎么诊断**：`Get-WinEvent` 查提供程序 `Application Hang`，能得到应用名与时刻；
`C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppHang_*` 存放报告。两者都不含调用栈——
未配置 `LocalDumps` 时 WER 不为挂起写转储——所以归档只能用来确认**确实挂了**以及挂在什么时候。

## 2. 被 XAML 引用的类型不能写成表达式体的 `as` + `switch`

WinUI 的标记编译器自行解析项目的 C# 源码来解析 `using:` 命名空间中的类型，它的解析器比 Roslyn 窄。
一个 `DataTemplateSelector` 的重写写成：

```csharp
protected override DataTemplate? SelectTemplateCore(object item) =>
    item as string switch { ... };
```

编译器对该选择器报 `WMC0001: Unknown type`，并且因为它就此放弃，文件中**其后每一个类型**都报
`Cannot resolve DataType`——包括已经正常编译了几个月的那些。这段 C# 本身合法，单独编译毫无问题。

**规则**：改用块体加 `is` 模式。新增一个类型后若出现一片 `WMC0909`，只修第一条 `WMC0001`，
其余全部是级联，不要逐条看。

## 3. ContentDialog 的宽度由 `ContentDialogMaxWidth` 主题资源钳制

在 ContentDialog 控件上设 `MaxWidth` 没有任何效果：模板按 `ContentDialogMaxWidth` 资源
（默认 **548**）确定自身尺寸，超出内边距之后剩余宽度的内容会**在卡片圆角边缘被静默裁掉**——
尾部按钮直接消失，不报任何错误。

**规则**：`SettingsDialog.xaml` 覆盖了该资源（672），并把每条固定栏宽都写明以适配其中；
要加宽必须同时加。高度方向存在同族资源 `ContentDialogMaxHeight`。

## 4. `.resw` 的键名不能以某个元素的 `x:Uid` 加点开头

资源加载器把每一个 `<Uid>.<Something>` 条目理解为「给 `x:Uid` 为 `Uid` 的元素设置属性 `Something`」。
那些只在代码里通过资源解析器取用的键（`TokenUsageCost.Estimate`、`TokenUsageMetric.TodayBilled` 等）
之所以能用，仅仅是因为没有元素带这个 `x:Uid`。

在一个带 `x:Uid="TokenUsageSpendCurve"` 的控件旁边加上键 `TokenUsageSpendCurve.Now`：编译通过、
smoke test 通过，然后已安装的面板一打开就崩，`Microsoft.UI.Xaml.dll` 里一个 stowed exception，
唯一可读的线索是 `%LOCALAPPDATA%\CrashDumps` 下转储里的字符串
`Unable to resolve property 'Now' while processing properties for Uid 'TokenUsageSpendCurve'`。

**规则**：只在代码中使用的键要用一个不是任何 `x:Uid` 的前缀（`TokenUsageCurve.Now`）。
面板以 `0xc000027b` 无消息退出时，**先 grep 转储里的 UTF-16 字符串**，再做别的。

## 5. `0xc000027b` 且转储里没有文本：挂 `UnhandledException`，在隔离实例里复现

XAML 回调中抛出的每一个托管异常（依赖属性变更、`x:Bind` getter、`DispatcherQueue` 回调）都表现为这种
stowed-exception fail-fast，而 WER 转储并不总是带着消息。花费曲线卡片的第二次崩溃是
`Microsoft.UI.Xaml.LayoutCycleException`——曲线的 `Path` 在 `SizeChanged` 里重建，其描边每轮把模板的
期望尺寸撑大半个像素。转储里没有任何信息说明这一点；一个临时的、把内容写到 `%TEMP%` 的
`Application.UnhandledException` 处理器第一次运行就说清了。

两件事让这个循环变快：

- **不依赖桌面就能复现**。`--smoke-test` 和启动器的面板 smoke 都不给卡片喂数据，所以它们都会通过。
  先结束已安装实例（它的管道名是生产常量，任何隔离实例都无法与之共存），再用
  `scripts/WinWidgetBoard.UiAutomation.psm1` 里的 `Start-TestBroker` / `Start-TestPanel`，
  配临时数据目录和新的 instance id：broker 仍会扫描真实转录，卡片几秒内就拿到真实数据，
  用 `Process.HasExited` 加上对 `TokenUsageCostText` 的一次 UIA 查找即可判断它是否活下来。
- **在 `SizeChanged` 里重建的东西不得影响布局**。把计算出的图形放进 `Canvas`（它的期望尺寸忽略子元素），
  尺寸在代码里设置；绝不让带描边的 `Path` 成为模板测量面板的直接子元素。

## 6. `x:Bind` 的属性名不能与同一页面上的 `x:Name` 重名

`MainWindow.xaml` 中一个 `DataTemplate` 针对自己的 `x:DataType` 绑定了 `Text="{x:Bind StatusText}"`，
而该页面同时有 `<TextBlock x:Name="StatusText">`——编译器会把它变成页面 partial 类上的一个字段。
两个名字冲突，而编译器没有报告冲突，它自己崩了：

```
Xaml Internal Error error WMC9999: ... could not find any resources appropriate for the
specified culture ... "Microsoft.UI.Xaml.Markup.Compiler.ErrorMessages.resources" ...
```

这条消息是编译器在格式化真正的错误时加载不到自己的错误字符串，因此它不指出文件、行号和符号。
**不要把它读作 NuGet 包损坏或 SDK 损坏。** 用二分法定位：故障就在这次改动的 XAML 里，
把被绑定的属性改名（`StatusText` → `MetricStatusText`）即可。页面级的 `x:Name` 是胜出的一方，
改 ViewModel 的属性名，不要改元素名。

## 7. `TextBox.TextChanged` 在之后的一个 tick 才触发；`ItemsRepeater` 不为 `x:Bind` 模板设置 `DataContext`

两条各自付出过一轮真机排查的 WinUI 事实：

- 给 `TextBox.Text` 赋值**不会**在 setter 内部触发 `TextChanged`，它在下一个布局 tick 之后才触发。
  于是「抑制重入」的标志位在赋值前置起、赋值后清掉，等处理器真正运行时它已经落下了；
  而 UIA 的 `ValuePattern.SetValue` 紧接一次 `Invoke` 时，点击处理器可能先于文本处理器运行。
  正确做法是让处理器对状态幂等（便签搜索框把「输入框为空、查询本来也为空」视为无事可做），
  而不是依赖触发顺序。
- 由 `CardItemsRepeater` 实现出的元素可能没有 `DataContext`，因为卡片模板用 `x:Bind` 绑定。
  要从一个元素解析出卡片，用 `CardItemsRepeater.GetElementIndex` / `TryGetElement`
  （`ResolveCurrentCardSurfaceItem`、`RealizedNoteCardRoots`），绝不要用
  `DataContext is CardSurfaceItem`。
