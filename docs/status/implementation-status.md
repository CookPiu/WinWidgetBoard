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
| 任务栏入口 | 已改为嵌入任务栏条带的自适应信息条，位置与内容可由右键菜单切换并持久化；天气模式显示图标 + 温度，并画与卡片同源的平面插画；单台参考机的入口到面板延迟与空闲占用已测量 | 发布前补完整显示矩阵和多设备性能 |
| 面板 | 已实现 WinUI 壳层、Desktop Acrylic、系统强调色层级、圆角阴影、锚定动效、关闭和模态保护；关闭改为隐藏并常驻进程，重开约 27～42 ms | 继续拆分集中式 code-behind，并在真实使用后校准细节 |
| 基础布局 | 已实现 2/4/6 列、拖动、持久化、恢复和撤销 | 只修缺陷，不扩展复杂编辑 |
| 便签 | 已实现 CRUD、搜索、Markdown、自动保存和安全删除；编辑区使用内容优先与渐进操作 | 只维护核心旅程 |
| 天气 | 已实现 Open-Meteo、订阅、手动位置和重启恢复设置；面板关闭时按小时后台刷新，任务栏入口可经 `weather.summary.get` 显示读数 | 自动定位和跨重启 payload 缓存延期 |
| 硬件监控 | 卡片与任务栏分段显示均已实现，显示项两处独立可配置并落盘；公开 API 层覆盖 8 项读数，采样按需进行 | 温度、风扇、CPU 频率恒为不可读，不再等待传感器层（[ADR-0029](../adr/0029-drop-the-bundled-sensor-driver.md)） |

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

- Release UnitTests：380/380；
- CoreBroker 与 WorkspacePanel Release x64 构建：0 警告、0 错误；
- `WinWidgetBoard.CoreBroker.exe --pipe-handshake-smoke-test`、`WinWidgetBoard.WorkspacePanel.exe --smoke-test` 与 `--broker-smoke-test`（配真实 Broker）退出码均为 0；
- 真实桌面 `Test-CardDragInteraction.ps1 -WithBroker` 通过（`REAL-DRAG-PASS`）：四种卡片的拖动柄、卡面、交互控件隔离和 `Esc` 取消经真实鼠标验证，握手、`cards.subscribe`、面板可见性上报和 `layout.save` 全部经真实命名管道走重构后的分发路径；
- 真实桌面 `Test-WeatherSettingsInteraction.ps1` 在移除脚本侧等待、直接把「取消对话框后立即点关闭」作为回归的前提下连续 3 次完整通过：`REAL-WEATHER-SETTINGS-PASS`（保存经真实面板写入 SQLite 并触发 provider 运行时重载）、`REAL-WEATHER-SETTINGS-RESTART-PASS`（重启后 `weather.settings.get` 读回 Tokyo / 35.6762 / 139.6503）和 `WINDOW-EXIT-PASS exitCode=0`；
- CI（`windows-2025-vs2026` 托管镜像）Debug 与 Release 双配置全绿：整解决方案 `msbuild` 构建、380/380 单测、LauncherHost 与 WorkspacePanel 的 `--smoke-test` 全部通过；
- 真实桌面 `Test-LauncherEntryPlacement.ps1` 通过：入口落在任务栏条带 1904..2000 内，
  入口外的条带点位归属其他进程（穿透成立），点击开启面板；
  `--geometry-smoke-test` 与 `--entry-visual-smoke-test` 退出码均为 0。
  该脚本必须在 DPI 感知的宿主（`pwsh`）里运行：Windows PowerShell 5.1 是 DPI 非感知进程，
  `GetWindowRect` 会返回虚拟化后的坐标，200% 缩放下把宽度报成一半并误判为越界；
- 真实桌面：Broker 与入口同时运行、无面板打开时，入口显示真实读数（Open-Meteo 真实请求），
  实测安装态入口为 `16,1912 - 240,1992`（112×40 DIP，DPI 192），晴天配色与日轮图形已截图核对；
  同一构建以 `--entry-icon-preview` 输出 192 DPI 对照表，十一种天气 × 深/浅条带逐格评审；
- 真实桌面 `Test-SystemMonitorInteraction.ps1` 通过（`REAL-SYSMON-PASS`）：卡片经 UIA 播报
  `内存 60% 18.8 GB / 31.4 GB`，在对话框中切换 `cpu.clock` 后卡片在一个刷新周期内跟随，
  重启面板后设置从 SQLite 读回；
- 真实桌面：安装态入口同时显示主胶囊与硬件胶囊，实测窗口 `16,1912 - 656,1992`（320×40 DIP，
  DPI 192），其中主胶囊为时钟、硬件胶囊为 CPU/MEM 与上下行两列；`--entry-icon-preview` 的
  192 DPI 对照表新增「单独硬件胶囊」与「主胶囊 + 硬件胶囊」两组，逐格评审字号与走势图；
- 真实桌面 `Test-LauncherEntryPlacement.ps1`（`--no-broker`，因此没有读数、只画主胶囊）实测
  `16,1912 - 312,1992`，148×40 DIP；
- 真实桌面 `Test-AddCardInteraction.ps1` 通过（`REAL-ADDCARD-PASS`）：编辑模式外添加按钮不可见，
  进入编辑后出现；全部卡片在板时不弹空对话框；移除计时器卡片后经选择器加回，完成编辑并重启面板后
  仍在布局中；
- `WinWidgetBoard.CoreBroker.exe --sysmon-smoke-test` 退出码 0，参考机上 8 项读数为 `ready`，
  温度、风扇、CPU 频率 4 项为 `unavailable`；
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
9. 入口与任务栏、以及其他 topmost 第三方任务栏扩展共享层级。**Explorer 在处理窗口激活时会把
   `Shell_TrayWnd` 重新提到 topmost**，从而短暂盖住入口——这就是「遇到最大化窗口时胶囊会闪一下」
   的成因（不是可见性，也不是重定位：实测窗口始终 `vis=True`、矩形不变，只是 z-order 被压下去）。
   现已改为在 `EVENT_SYSTEM_FOREGROUND` 上立即重置 topmost，并在其后 40 ms 三连补刀，覆盖
   Explorer 先于或后于我们重排的两种次序；500 ms 可见性轮询保留为兜底。
   参考机实测（11 次「新建窗口 → 激活 → 最大化」）：仅轮询时出现 1 次覆盖、持续 338 ms；
   加入 hook 后连续 3 轮均为 **0 次**。真实回归仍须先等待入口占据自身中心点再点击。
10. 计时器、待办和日历以延期占位卡保留在默认工作区，已确认维持现状；它们只作为布局占位，不增加业务行为，也不再作为待决问题。

## 6. 当前工作

本轮已完成（硬件监控，[ADR-0028](../adr/0028-system-monitor-scope-and-sensor-tiers.md)）：

- **范围变更**：核心五项变为核心六项。`ADR-0022` 将系统监控列为延期项并要求明确再批准，
  本轮即该再批准的落地；
- **公开 API 层**覆盖 CPU 占用、内存、GPU 占用与显存、磁盘活动与占用、网络上下行共 8 项，
  全部走 `GetSystemTimes` / `GlobalMemoryStatusEx` / `NetworkInterface` / `DriveInfo` / PDH，
  无新依赖、无驱动、无管理员；PDH 一律用 `PdhAddEnglishCounter`，否则在中文 Windows 上静默失效；
- **温度、风扇、CPU 频率报 `unavailable`**，等待尚未实现的传感器服务。CPU 频率归入该层是实测结论：
  `Processor Frequency` 在参考机上恒返回 1.33 GHz（实际约 4.2 GHz），
  而 `% Processor Performance × ~MHz` 得到 8.18 GHz，因为 `~MHz` 记录的是开机频率而非基准频率；
- **采样按需**：卡片可见或入口近 10 秒内索要过摘要才以 2 秒节奏采样，否则完全休眠，
  唤醒时丢弃增量基线；
- **卡片与任务栏各自独立配置**，每处最多 8 项，顺序即列表顺序，经版本化 IPC 落盘；
- **入口是两枚胶囊**：左边主胶囊（指示器 + 时间或天气，96～280 DIP），间隙 8 DIP，
  右边硬件胶囊（上限 440 DIP，无下限，没有读数时整枚不画）。硬件监控因此是独立开关而非
  第三种显示内容，天气与读数可以同时存在；旧的 `Content = 2` 配置在读取时迁移成
  「时钟 + 显示硬件监控」；
- **硬件胶囊两行密排**：两个读数为一列，列间 1 DIP 细线跨越两行；分段字号 10 DIP、图形 12 DIP；
  装不下的**整列**省略而不半截截断，落单读数居中；条带放不下两枚时先丢硬件胶囊而不是入口本身；
  入口不出现任何需要翻译的文字；
- **宽度滞回改为非对称**：原来只要测得宽度变化不足 8 DIP 就不重排，内容却可能已经超出预留宽度；
  胶囊放不下时不截断而是整列丢弃，于是**默认配置下最后一列（网速）会间歇性消失**。
  参考机复刻测量：CPU/MEM 固定、网速文本让整条在 136～157 DIP 之间摆动（21 DIP），
  例如已预留 152 DIP 时测得 154 DIP，差 2 DIP 落在滞回带内 → 网速列被丢。
  现在增长只要装不下就立刻重排，收缩要等空余累积到 16 DIP 才回收，每次重排多留 8 DIP 余量；
  实测 70 秒变速下载期间入口宽度 0 次变动、网速列全程在位；
- **字体按名字取 Semibold**：向 `Segoe UI` 家族请求 `FW_SEMIBOLD` 在本机解析成 `tmWeight 700`
  （GDI 字体映射向上取到 Bold 面），这个尺寸下全粗体会把字腔填死、边缘发毛；改为直接指定
  `Segoe UI Semibold` face 后解析为 600。同时文本蒙版改用 ClearType 渲染但按三个子像素样本
  **取平均**而非取最大值——等于水平方向三倍采样的灰度覆盖率，且因为最终只用一个颜色合成，
  不可能产生彩边。实测同一区域：满覆盖像素 545 → 490（笔画变轻），边缘半覆盖像素 598 → 735；
- **每个读数画在自己的历史走势之上**：最近 60 个采样（2 秒节奏 ≈ 2 分钟）由 Broker 归一化为
  0..1 随 `sysmon.summary.get` 下发，入口只把它映射成一层填充面积。有上限的指标按自身上限绘制，
  速率按窗口峰值相对绘制并设 4 KB/s 下限门槛，读不到的指标不画图。

**传感器层已验证并放弃**（[ADR-0029](../adr/0029-drop-the-bundled-sensor-driver.md)）：
以管理员运行 LibreHardwareMonitorLib 0.9.6，CPU 温度、CPU 频率、风扇**全部无读数**，
该次运行未安装任何服务——提权不是瓶颈，驱动本身没有加载。唯一多出来的是硬盘温度 45 °C
（NVMe SMART，需管理员）。同机 `R0TrafficMonitor` 与 `RTCore64` 均在运行，说明本机允许内核驱动加载，
被拦下的是该库分发的那个具体驱动。

因此不引入第三方运行时依赖、不新增第四个进程、不分发任何 WinRing0 系驱动（含改名变体）。
温度、风扇、CPU 频率四项恒显示「此电脑无法读取」。

上一轮已完成（天气三项优化）：

- **入口改为图标 + 温度，去掉地区**：地区是用户自己设的，每分钟重复一遍只是在花掉入口宽度。
  Provider 增加 `is_day`，并把 WMO 码归约成 `conditionIconId` 标记放进 Contracts，
  由 Broker、面板卡片和原生入口共用一张表；入口用自己的合成器画出十一个图形
  （圆、圆角矩形和一个多边形），不引入图标字体，单色跟随文字颜色；
- **卡片获得随天气变化的平面插画**：底色 + 一到三个图形放在卡片右下角空白处并溢出圆角，
  裁剪到卡片自身圆角，不接收指针输入、不带自动化名称；配色集中在 `WwbWeather*` 主题画刷，
  高对比度全部清零。`UT-UI-002` 由「禁止一切字面颜色」收紧为「字面颜色只能出现在天气插画画刷上，
  且必须在高对比度里被清零」——是加强而不是放宽；
- **位置设置改为地点搜索**（[ADR-0027](../adr/0027-weather-location-search.md)）：
  新增 `weather.locations.search`，经 Open-Meteo 地理编码端点解析地名，返回最多 8 条候选；
  对话框改为「搜索 → 选择 → 保存」，经纬度只读展示；搜索只在提交时发生，不做按键即搜。
  这是第一条把用户输入文本送出本机的路径，隐私文档已相应更新；
- 该方法是唯一的异步命令，单独在管道循环里 await，不占用路由器全局锁；
- **入口获得与卡片同源的天气插画**：整枚胶囊铺天空色，尾部预留 26 DIP 画当前天气的图形，
  越过收尾圆头少许并被胶囊裁切。配色是 `WwbWeather*` 的重述（入口是分层窗口，没有
  `ThemeResource`），`WorkspaceVisualStyles.xaml` 仍为权威；`unknown` 与非天气模式不画也不预留，
  高对比度完全不画。插画画在中性底色**之上**——画在其下时天空与图形都会被冲淡成一团灰；
- 共享 UIA 模块增加 `Save-WindowCapture`：用 `PrintWindow` 抓取窗口自身内容，
  不再受 z 序与焦点影响——常驻面板会因失焦隐藏，屏幕区域截图因此并不可靠。

修复了一个真实缺陷：**卡片一直丢弃 Broker 推送的快照**。
`sequence` 有两个互不相干的计数器 —— Broker 的，以及面板自己
（占位快照、可见性调度）的 —— 而 `ApplySnapshot` 把两者放在同一个
字段上比较。面板先跑一会儿后本地序号就超前了，之后 Broker
推送的真实天气全部被当作「更旧」丢弃，卡片停在占位态。
现在 Broker 快照只在它自己的序列空间内比较，首条无条件生效
（`UT-CARD-097`～`UT-CARD-099`）。`Test-WeatherProviderInteraction.ps1` 因此由失败转为
`REAL-WEATHER-PASS temperature="29.9 °C"`。

本轮修正的一处判断错误：先前认为「天气卡片收不到数据」是既有缺陷，实际上是我一直在运行
**前一天的面板产物**。`Install-WinWidgetBoard.ps1` 与真实桌面脚本读取的是 `x64\Release`，
而 `dotnet build -c Release` 不带 `-p:Platform=x64` 写的是另一个路径。加上 `-p:Platform=x64`
后卡片订阅正常：`cards.subscribe` 返回 `ready` 快照并被应用。该陷阱已写入 CLAUDE.md。

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
