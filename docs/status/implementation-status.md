# 实施状态

最后更新：2026-08-20
当前策略：轻量核心版
当前代码基线：本文件所在提交

## 1. 当前结论

入口、面板、基础布局、便签和天气已经形成可运行链路。项目停止扩展新卡片和平台能力，当前优先修复核心工作台的可用性、视觉密度和维护债务。

WorkspacePanel 已完成“静谧画布”视觉收口：减少多层边框和外围留白，把标题、日期、搜索与全局操作合并为单行头部，并统一缩小字号、间距、控件和网格尺度。面板使用系统 Desktop Acrylic、强调色微光和圆角，卡片使用更实的系统语义表面与轻量阴影；便签默认只保留标题、正文、新建和更多操作，Markdown、复制、删除及恢复命令按状态渐进呈现。面板、拖动回弹及渐进浮层采用克制且可反向的动效，不改变布局、便签、天气或 IPC 数据合同。

## 2. 核心五项

| 能力 | 当前状态 | 仍需处理 |
| --- | --- | --- |
| 任务栏入口 | 已改为嵌入任务栏条带的自适应信息条，显示日期时间，位置与内容可由右键菜单切换并持久化；单台参考机的入口到面板延迟与空闲占用已测量 | 发布前补完整显示矩阵和多设备性能 |
| 面板 | 已实现 WinUI 壳层、Desktop Acrylic、系统强调色层级、圆角阴影、锚定动效、关闭和模态保护；关闭改为隐藏并常驻进程，重开约 27～42 ms | 继续拆分集中式 code-behind，并在真实使用后校准细节 |
| 基础布局 | 已实现 2/4/6 列、拖动、持久化、恢复和撤销 | 只修缺陷，不扩展复杂编辑 |
| 便签 | 已实现 CRUD、搜索、Markdown、自动保存和安全删除；编辑区使用内容优先与渐进操作 | 只维护核心旅程 |
| 天气 | 已实现 Open-Meteo、订阅、手动位置和重启恢复设置；面板关闭时按小时后台刷新，任务栏入口可经 `weather.summary.get` 显示读数 | 自动定位和跨重启 payload 缓存延期 |

## 3. 已有基础设施

- 当前用户 Named Pipe、握手、心跳、重连和版本化 Envelope；
- CoreBroker SQLite migration、备份边界和 revision 保护；
- 卡片不可变快照、连接级订阅、有界背压和 UI dispatcher；
- 集中 Provider 调度、超时、退避、可见性暂停和 fatal fault 边界；
- 验收进程身份与临时数据库隔离；
- 双语资源和基础无障碍锚点。

这些能力进入维护状态，不再继续平台化。

## 4. 最近完成证据

当前提交的完成证据：

- Release UnitTests：309/309；
- CoreBroker 与 WorkspacePanel Release x64 构建：0 警告、0 错误；
- `WinWidgetBoard.CoreBroker.exe --pipe-handshake-smoke-test`、`WinWidgetBoard.WorkspacePanel.exe --smoke-test` 与 `--broker-smoke-test`（配真实 Broker）退出码均为 0；
- 真实桌面 `Test-CardDragInteraction.ps1 -WithBroker` 通过（`REAL-DRAG-PASS`）：四种卡片的拖动柄、卡面、交互控件隔离和 `Esc` 取消经真实鼠标验证，握手、`cards.subscribe`、面板可见性上报和 `layout.save` 全部经真实命名管道走重构后的分发路径；
- 真实桌面 `Test-WeatherSettingsInteraction.ps1` 在移除脚本侧等待、直接把「取消对话框后立即点关闭」作为回归的前提下连续 3 次完整通过：`REAL-WEATHER-SETTINGS-PASS`（保存经真实面板写入 SQLite 并触发 provider 运行时重载）、`REAL-WEATHER-SETTINGS-RESTART-PASS`（重启后 `weather.settings.get` 读回 Tokyo / 35.6762 / 139.6503）和 `WINDOW-EXIT-PASS exitCode=0`；
- CI（`windows-2025-vs2026` 托管镜像）Debug 与 Release 双配置全绿：整解决方案 `msbuild` 构建、309/309 单测、LauncherHost 与 WorkspacePanel 的 `--smoke-test` 全部通过；
- 真实桌面 `Test-LauncherEntryPlacement.ps1` 连续 3 次通过：入口落在任务栏条带 1904..2000 内，
  实测 `16,1912 - 304,1992`（144×40 DIP，DPI 192），入口外的条带点位归属其他进程（穿透成立），
  点击开启面板；`--geometry-smoke-test` 与四项 LauncherHost smoke 退出码均为 0；
- 真实桌面：Broker 与入口同时运行、无面板打开时，入口显示 `32° Singapore`（Open-Meteo 真实请求）；
- 验收使用隔离临时数据和独立实例身份，结束后已清理。

这属于 L2 针对性证据，不替代发布候选的完整显示器、偏好和无障碍矩阵。更早轮次的完成证据以 Git 提交和测试名称为准。

### 4.1 参考机启动与后台占用

测量条件：Intel Core Ultra 5 225H（14 逻辑核）、31.4 GB 内存、Windows 11 25H2 build 26200.9168（注册表 `ProductName` 仍显示 `Windows 10 Pro`）、Release x64、未附加调试器、基线提交 `eb07d3e`、工具 `scripts/Measure-StartupFootprint.ps1`、每阶段 5 次迭代、空闲采样 60.9 秒、隔离临时数据目录。

| 指标 | 最小 | 中位 | 最大 |
| --- | --- | --- | --- |
| 面板进程启动 → 首个窗口 | 661.2 ms | 669.3 ms | 673.6 ms |
| 面板进程启动 → 布局就绪状态 | 872.2 ms | 880.0 ms | 928.1 ms |
| 任务栏入口点击 → 面板窗口 | 678.9 ms | 695.6 ms | 720.0 ms |

无面板打开时的空闲占用（后台天气启用**前**）：CoreBroker 在 60.9 秒内消耗 109 ms CPU（全核 0.013%），工作集 43.1～46.4 MB，中位 43.9 MB；LauncherHost 消耗 0 ms CPU，工作集恒为 12.1 MB。

启用每小时后台天气刷新**后**，同机 90 秒空闲采样：CoreBroker 消耗 422 ms CPU（全核 0.033%），工作集 52.0～56.8 MB，中位 52.8 MB；LauncherHost 消耗 78 ms CPU（全核 0.006%），工作集 11.6 MB。CoreBroker 的 CPU 包含采样窗口内那一次启动刷新，不代表每小时稳态。

### 4.2 面板冷启动耗时构成与常驻结果

用 `WorkspacePanel.exe --startup-trace`（默认关闭，只写 stderr）在同一次运行内分段。**跨运行的绝对值不可比**——本会话后期机器负载明显升高，同一构建的 `layout-ready` 从约 980 ms 漂到约 1730 ms；下表取同一次运行内的分段与占比。

| 阶段 | 占比 | 说明 |
| --- | --- | --- |
| 进程 + .NET 运行时 + WinAppSDK 启动 | 约 25～30% | 我们的第一行代码之前 |
| `App` 构造与 XAML 框架初始化 | 约 5～7% | |
| `MainWindow` 的 `InitializeComponent()` | 约 28～30% | 单个 62 KB XAML |
| `MainWindow` 构造函数后半段 | 约 22～25% | 其中约 105 ms 是 ViewModel 构造，约 170 ms 是窗口互操作与几何：`ConfigureToolWindow`、`AppWindow` 取用、`PanelGeometry.Calculate`、`ApplyPlacement` |
| 激活 + Broker 往返 + 布局绑定 | 约 10～13% | |

结论：耗时集中在运行时/框架初始化、单个大 XAML 的展开，以及**必须在显示前完成**的窗口与几何设置。构造函数后半段那 170 ms 不可延迟——推迟只会把代价挪到激活时。因此「延迟加载 + ReadyToRun」这条低成本路径**没有明显可摘的果实**。

`PublishReadyToRun` 未采用：`dotnet publish` 产物不含 `resources.pri`，`ResourceLoader` 直接抛 `FileNotFoundException`（退出码 20）；手工补齐 pri 后的测量受目录与环境差异污染，不构成有效对比。要走这条路必须先修好发布配置，属于独立工作。

已按 [ADR-0025](../adr/0025-resident-workspace-panel.md) 实施常驻：关闭隐藏窗口并保留进程，隐藏时调用 `EmptyWorkingSet` 交还页面。实测结果：

| 指标 | 常驻前 | 常驻后 |
| --- | --- | --- |
| 入口点击到面板可见 | 约 740 ms | 27～42 ms（含 UIA 轮询，属上界；裸窗口可见性测得 9～17 ms） |
| 面板工作集（隐藏时） | 进程已退出 | 18～37 MB |
| 面板工作集（显示时） | 178～249 MB 峰值 | 60～66 MB |
| 面板空闲 CPU | 进程已退出 | 94 ms/30 秒（全核 0.022%） |

启动器驱动的 8 次连续开关全部符合预期。常驻同时暴露并修复了一个状态漂移：面板会因失焦自行隐藏而不通知启动器，而旧的复位路径依赖进程退出；启动器现在以面板窗口的真实可见性为准。

这是单台参考机的一次测量，不覆盖多显示器、DPI 缩放、低配设备和长稳；发布候选的性能矩阵仍未执行。上表的延迟数字取自后台天气启用前的那次运行；之后的复测在机器负载明显更高时进行，与之不可直接比较，故未替换。

## 5. 当前技术债务

1. `MainWindow.xaml.cs` 仍同时协调便签删除/编辑、拖动和设置；卡片订阅、布局持久化、便签列表和动效已提取。
2. `CoreBrokerCommandRouter.cs` 只保留 `session.ping`、方法分发和连接释放；便签、布局、天气设置、卡片订阅和面板可见性各自成为领域 handler，共用同一把锁与同一份操作缓存上限。
3. `ProviderRefreshScheduler.cs` 单文件状态机过大。
4. UIA 公共窗口、元素、输入和进程辅助函数已提取到 `scripts/WinWidgetBoard.UiAutomation.psm1`；查找一律限定在目标进程自己的顶层窗口内，并对可重试的 UIA COM 故障退避重试。
5. Windows App SDK 自包含输出较大，开发构建不应长期留在仓库。
6. 远程仓库 `CookPiu/WinWidgetBoard`（私有）已配置。CI 在 GitHub 托管镜像 `windows-2025-vs2026` 上 Debug 与 Release 双配置全绿，单个 job 约 2 分钟，已按 `push` / `pull_request` 自动触发，纯 Markdown 改动不触发。该镜像自带 VS Enterprise 2026 `18.8.12023.21`、Windows SDK `10.0.26100.0`、.NET SDK `10.0.302` 和 `VC.14.44.17.14.x86.x64` 工具集，项目锁定的 `VCToolsVersion 14.44.35207` 解析正常，无需放宽任何锁定值。
7. 整解决方案构建已在 CI 上验证通过，但仍无法在本机进行：本机 VS MSBuild 解析不到 `Microsoft.NET.Sdk`，设置 `MSBuildSDKsPath` 也只能多走一步，随后卡在 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator`。本地仍按分项目构建。
8. 完整显示、无障碍、性能和发布矩阵尚未执行。
9. 入口与其他 topmost 第三方任务栏扩展共享层级，可能被短暂压住，直到下一次 `EnsureTopmost`；
   参考机上已观察到与一个系统监视器组件互相覆盖。真实回归必须先等待入口占据自身中心点再点击。
10. 计时器、待办和日历以延期占位卡保留在默认工作区，已确认维持现状；它们只作为布局占位，不增加业务行为，也不再作为待决问题。

## 6. 当前工作

本轮已完成：

- 修复常驻遗留的孤儿进程缺口（[ADR-0026](../adr/0026-launcher-owned-process-tree.md)）：LauncherHost 建立
  `KILL_ON_JOB_CLOSE` 的 Job Object，面板与 Broker 都在 `CREATE_SUSPENDED` 下先入 Job 再恢复；
  对启动器执行 `Process.Kill()` 后三个进程全部消失（实测）；
- LauncherHost 自启动 CoreBroker：`BCryptGenRandom` 生成令牌并写入自身环境块，令牌不再经由命令行；
  外部已提供会话（开发脚本）时不接管，`--no-broker` 可显式关闭，真实桌面测试据此保持数据隔离；
- 子进程路径解析新增「同目录下的同名子目录」一档，安装布局因此可让三个 .NET 应用各自保留依赖集；
- 新增 `scripts/Install-WinWidgetBoard.ps1`：安装到 `%LOCALAPPDATA%\WinWidgetBoard\app`，建立开始菜单与
  登录启动快捷方式，`-Uninstall` 完整回退；仅当前用户，无提权、无服务、无注册表类注册；
- 入口渲染由「色键透明 + `RoundRect` + `GetSysColor(COLOR_BTNFACE)`」改为预乘 BGRA 位图 +
  `UpdateLayeredWindow`：胶囊边缘、指示器与文字都带真实逐像素 alpha，不再有一位透明度的锯齿边；
  同时移除 `SetWindowRgn`，命中判定改为胶囊本体的有符号距离场；
- 入口获得悬停、按下（缩放 `0.97`）与「面板已打开」三种状态，各由一条与面板开合同角频率
  （`15.4919`）的临界阻尼弹簧驱动；打开态的判据是指示器由圆点长成竖条，不只靠颜色；
  动画定时器只在迁移期间存在，静息入口无定时器唤醒；`SPI_GETCLIENTAREAANIMATION` 关闭时退化为淡入淡出；
- 颜色映射改为：强调色取 `COLOR_HIGHLIGHT`，中性底色由 `SystemUsesLightTheme` 在纯白/纯黑间选择，
  高对比度整体退回 `GetSysColor` 并恢复 1 DIP 系统边界；
- 新增 `--entry-visual-smoke-test` 并折叠进 `--smoke-test`，因此 CI 无需改动即覆盖：预乘不变量、
  圆角透明与抗锯齿、悬停步长、指示器随打开态增高、高对比度不透明，以及 96/144/192 dpi 三档；
- 共享 UIA 模块增加 `Move-PointerToPoint` 与 `Save-ScreenRegionCapture`，并把 `SendInput` 的
  绝对坐标归一化提取为共用实现。

## 7. 下一步

核心五项已全部具备真实桌面证据，远程与 CI 已建立。当前优先级由「继续改代码」转为「靠真实使用暴露问题」：

1. 补 ADR-0001 的显示矩阵：多显示器、100%～200% DPI、任务栏自动隐藏与左对齐、Explorer 重启、全屏。本轮只验证了参考机的底部居中任务栏。
   同时补跑 `scripts/Test-LauncherEntryPlacement.ps1` 的点击一段：本轮该机拒绝一切合成输入（`SetCursorPos` 返回 `FALSE` 且不设错误码，
   `SendInput` 报成功但指针不动），入口的窗口过程改为由 `PostMessage` 直接驱动验证，几何与穿透两段仍按原脚本通过。
2. **真实使用一段时间**，只记录可复现缺陷。本轮两个缺陷都由实际运行暴露，不是读代码发现的。
3. `MainWindow.xaml.cs` 的便签删除/编辑、拖动和设置协调**等下次真要改这些行为时顺带拆**，不单独开一轮；重构回报取决于后续还要改多少代码。
4. 常驻已落地（见 §4.2），孤儿进程已由 Job Object 根治（见 §6）。剩余相关项：重新显示沿用启动时解析的几何，跨显示器或 DPI 变化后的重显尚未验证；`PublishReadyToRun` 的发布配置仍未修好。
5. `ProviderRefreshScheduler.cs` 仅在其开始产生缺陷时再分解。

## 8. 延期

- 计时器、待办、剪贴板、日历和系统监控业务；
- 自动定位和跨重启天气 payload 缓存；
- Outlook、Google 和 Microsoft To Do；
- PluginHost、第三方插件和声明式 UI；
- 云同步、账号和遥测；
- 多布局场景和高级动效；
- 安装、更新、签名、SBOM 和企业发布门禁。

延期项不得作为当前“下一步”自动实施。

## 9. 待用户决定

- 正式名称与图标；
- 最终许可证；
- 何时进入发布候选阶段。
