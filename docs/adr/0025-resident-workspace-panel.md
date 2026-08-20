# ADR-0025：面板关闭改为隐藏，进程常驻

状态：Accepted
日期：2026-08-20
关系：修改 [ADR-0007](0007-launcher-panel-process-handoff.md) 的进程生命周期；依据 [ADR-0024](0024-background-provider-keepalive.md) 已放开的有界后台运行

## 背景

入口点击到面板出现约 740 ms。用 `--startup-trace` 分段后，耗时集中在三处：进程与 .NET/WinAppSDK 运行时启动（约 25～30%）、`MainWindow` 的 XAML 展开（约 28～30%）、以及显示前必须完成的窗口互操作与几何设置（约 22～25%）。

低成本方案先被排除：`PublishReadyToRun` 的发布产物缺 `resources.pri`，`ResourceLoader` 直接抛异常，要用它得先修发布配置；XAML 侧的大块内容本就在 `DataTemplate` 里、构造时不实例化，急切构建的是首屏内容；构造函数后半段那约 170 ms 是 `ConfigureToolWindow`、`AppWindow` 取用、`PanelGeometry.Calculate` 和 `ApplyPlacement`，推迟只会把代价挪到激活时。

这三块正好都是「进程只要活着就已经付过」的成本。

## 决策

1. 关闭面板不再结束进程：`CloseAfterMotion` 改为隐藏窗口并保留进程。失焦关闭、`Esc`、关闭按钮和系统关闭都走同一条路径。
2. 隐藏时调用 `EmptyWorkingSet` 把页面交还操作系统。
3. 重新显示由 `AppWindow.Changed` 的可见性变化驱动，不依赖 `Activated`——启动器的 `SetForegroundWindow` 不保证触发激活事件。
4. 启动器的开关状态改为**以面板窗口的真实可见性为准**（`WorkspacePanelProcess::IsPanelVisible`），不再自己记账。
5. 启动器退出时结束面板（`WorkspacePanelProcess::Shutdown`），避免留下孤儿进程。
6. `--panel-lifecycle-smoke-test` 由「关闭后退出」改为断言「关闭后隐藏且存活，再显示仍是同一进程」。

## 理由

- 收益直接对应已测出的耗时构成，不是猜测；
- `EmptyWorkingSet` 把常驻代价从「显示时的峰值」压回到隐藏时的小值，回应了「尽量减少资源占用」；
- 以真实可见性为准消除了一整类状态漂移：面板会因失焦自行隐藏而不通知启动器，而旧的复位路径依赖进程退出。

## 后果

参考机实测（Release x64）：

| 指标 | 常驻前 | 常驻后 |
| --- | --- | --- |
| 入口点击到面板可见 | 约 740 ms | **27～42 ms**（含 UIA 轮询，属上界） |
| 面板工作集（隐藏时） | 不存在（进程已退出） | **18～37 MB** |
| 面板工作集（显示时） | 约 178～249 MB 峰值 | 约 60～66 MB |
| 面板空闲 CPU | 不存在 | 94 ms/30 秒（全核 0.022%） |

负面与约束：

- 常驻多了一个进程。首次打开仍是完整冷启动，峰值工作集约 250 MB；隐藏后回落到 18～37 MB。
- **没有独立的退出协议**：`WM_CLOSE` 现在表示隐藏。启动器关闭面板时先发一次 `WM_CLOSE` 让其走完关闭路径，再终止进程。面板自身不持有未落盘状态（数据库归 CoreBroker），最坏情况是一次尚未发送的便签防抖自动保存。
- 启动器被强制结束（崩溃、任务管理器）时面板会成为孤儿。用 Job Object 绑定生命周期可以根治，本轮未做。
- 重新显示沿用启动时解析的几何，不重新解析显示器与入口矩形；跨显示器或 DPI 变化后的重显仍需验证。
- 所有断言「关闭后进程退出」的验收脚本都已改为断言「隐藏且存活」。

## 放弃方案

- **预热而非常驻**：面板仍在关闭时退出，由启动器提前拉起一个隐藏实例。复杂度相当，但每次关闭后都要重新预热，且预热窗口内行为不确定。
- **仅在最近使用时常驻（空闲若干分钟后退出）**：能进一步压低平均内存，但引入定时器与两种关闭语义；等真实使用证明内存是问题时再考虑。
- **在验收模式下保留退出语义**：会让生产环境的关闭路径失去真实桌面覆盖。

## 验证

- `--panel-lifecycle-smoke-test` 覆盖隐藏、存活与同进程重显；
- 启动器驱动的 8 次连续开关全部符合预期，开启耗时 27～42 ms；
- `Test-WeatherSettingsInteraction.ps1`、`Test-WeatherProviderInteraction.ps1`、`Test-CardRuntimeStatusInteraction.ps1` 改为 `WINDOW-HIDDEN-PASS`；
- `Test-CardDragInteraction.ps1 -WithBroker` 与 `Test-LauncherEntryPlacement.ps1` 回归通过。
