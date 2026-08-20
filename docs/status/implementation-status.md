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
| 任务栏入口 | 已实现公开 Win32 几何、点击、穿透和面板交接；单台参考机的入口到面板延迟与空闲占用已测量 | 发布前补完整显示矩阵和多设备性能 |
| 面板 | 已实现 WinUI 壳层、Desktop Acrylic、系统强调色层级、圆角阴影、锚定动效、关闭和模态保护；完成单行头部与紧凑尺度收口 | 继续拆分集中式 code-behind，并在真实使用后校准细节 |
| 基础布局 | 已实现 2/4/6 列、拖动、持久化、恢复和撤销 | 只修缺陷，不扩展复杂编辑 |
| 便签 | 已实现 CRUD、搜索、Markdown、自动保存和安全删除；编辑区使用内容优先与渐进操作 | 只维护核心旅程 |
| 天气 | 已实现 Open-Meteo、订阅、手动位置和重启恢复设置；摘要改为紧凑横向信息层级 | 自动定位和跨重启 payload 缓存延期 |

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
- 验收使用隔离临时数据和独立实例身份，结束后已清理。

这属于 L2 针对性证据，不替代发布候选的完整显示器、偏好和无障碍矩阵。更早轮次的完成证据以 Git 提交和测试名称为准。

### 4.1 参考机启动与后台占用

测量条件：Intel Core Ultra 5 225H（14 逻辑核）、31.4 GB 内存、Windows 11 25H2 build 26200.9168（注册表 `ProductName` 仍显示 `Windows 10 Pro`）、Release x64、未附加调试器、基线提交 `eb07d3e`、工具 `scripts/Measure-StartupFootprint.ps1`、每阶段 5 次迭代、空闲采样 60.9 秒、隔离临时数据目录。

| 指标 | 最小 | 中位 | 最大 |
| --- | --- | --- | --- |
| 面板进程启动 → 首个窗口 | 661.2 ms | 669.3 ms | 673.6 ms |
| 面板进程启动 → 布局就绪状态 | 872.2 ms | 880.0 ms | 928.1 ms |
| 任务栏入口点击 → 面板窗口 | 678.9 ms | 695.6 ms | 720.0 ms |

无面板打开时的空闲占用：CoreBroker 在 60.9 秒内消耗 109 ms CPU（全核 0.013%），工作集 43.1～46.4 MB，中位 43.9 MB；LauncherHost 消耗 0 ms CPU，工作集恒为 12.1 MB。

这是单台参考机的一次测量，不覆盖多显示器、DPI 缩放、低配设备和长稳；发布候选的性能矩阵仍未执行。

## 5. 当前技术债务

1. `MainWindow.xaml.cs` 仍同时协调便签删除/编辑、拖动和设置；卡片订阅、布局持久化、便签列表和动效已提取。
2. `CoreBrokerCommandRouter.cs` 只保留 `session.ping`、方法分发和连接释放；便签、布局、天气设置、卡片订阅和面板可见性各自成为领域 handler，共用同一把锁与同一份操作缓存上限。
3. `ProviderRefreshScheduler.cs` 单文件状态机过大。
4. UIA 公共窗口、元素、输入和进程辅助函数已提取到 `scripts/WinWidgetBoard.UiAutomation.psm1`；查找一律限定在目标进程自己的顶层窗口内，并对可重试的 UIA COM 故障退避重试。
5. Windows App SDK 自包含输出较大，开发构建不应长期留在仓库。
6. 远程仓库 `CookPiu/WinWidgetBoard`（私有）已配置。CI 在 GitHub 托管镜像 `windows-2025-vs2026` 上 Debug 与 Release 双配置全绿，单个 job 约 2 分钟，已按 `push` / `pull_request` 自动触发，纯 Markdown 改动不触发。该镜像自带 VS Enterprise 2026 `18.8.12023.21`、Windows SDK `10.0.26100.0`、.NET SDK `10.0.302` 和 `VC.14.44.17.14.x86.x64` 工具集，项目锁定的 `VCToolsVersion 14.44.35207` 解析正常，无需放宽任何锁定值。
7. 整解决方案构建已在 CI 上验证通过，但仍无法在本机进行：本机 VS MSBuild 解析不到 `Microsoft.NET.Sdk`，设置 `MSBuildSDKsPath` 也只能多走一步，随后卡在 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator`。本地仍按分项目构建。
8. 完整显示、无障碍、性能和发布矩阵尚未执行。
9. 计时器、待办和日历以延期占位卡保留在默认工作区，已确认维持现状；它们只作为布局占位，不增加业务行为，也不再作为待决问题。

## 6. 当前工作

本轮已完成：

- 将 `CoreBrokerCommandRouter` 拆为领域 handler：`LayoutCommandHandler`、`WeatherSettingsCommandHandler`、`CardSubscriptionCommandHandler` 和 `PanelVisibilityCommandHandler`，与既有 `NoteCommandHandler` 对齐；
- 路由器只保留 `session.ping`、按 Contract `Methods` 分发和连接释放，可用性标志与面板可见性状态改为向对应 handler 转发；
- 全部 handler 继续共用路由器的同一把锁，域间序列化行为不变；操作缓存上限统一由 `CoreBrokerCommandSupport.MaxCachedOperations` 提供；
- 公开构造签名、`Handle` 重载、`RemoveConnection` 和四个可用性标志保持不变，IPC 方法、限制和错误码未改动；
- 新增 `scripts/Measure-StartupFootprint.ps1`，把入口到面板延迟和空闲占用变成可重复测量；窗口按类名查找和左键点击进入共享 UIA 模块；
- 天气设置真实流程改为只在面板进程自己的顶层窗口内查找元素，不再遍历桌面根；共享模块对可重试的 UIA COM 故障退避重试，该流程首次完整跑通；
- 修复 `UT-CARD-096` 与孤儿请求降级之间的竞争：被取消但 provider 忽略取消的执行是在完成回调里由 Active 异步降级为 Predecessor，而 `InFlightCount` 是两者之和，降级前后都读作 1，无法作为等待条件；测试改为有界重试 pump 直到真正启动一次刷新。该测试在 14 核本机几乎必过，在双核 CI 上必挂；
- 修复面板在模态作用域仍被持有时静默丢弃关闭请求的缺陷：`RequestCloseMotion` 改为记下延迟请求，最后一层模态作用域释放时补发一次；判定逻辑放在无 WinUI 依赖的 `PanelActivationClosePolicy` 并有单测覆盖，失焦关闭与 `Esc` 路径不受影响。

## 7. 下一步

核心五项已全部具备真实桌面证据，远程与 CI 已建立。当前优先级由「继续改代码」转为「靠真实使用暴露问题」：

1. **真实使用一段时间**，只记录可复现缺陷。本轮两个缺陷都由实际运行暴露，不是读代码发现的。
2. `MainWindow.xaml.cs` 的便签删除/编辑、拖动和设置协调**等下次真要改这些行为时顺带拆**，不单独开一轮；重构回报取决于后续还要改多少代码。
3. 性能暂不优化。首帧约 670 ms 属自包含 WinUI 正常范围，既无目标值也无实际抱怨；若要动，先做耗时构成分解，不能只凭总量。
4. `ProviderRefreshScheduler.cs` 仅在其开始产生缺陷时再分解。

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
