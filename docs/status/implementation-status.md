# 实施状态

最后更新：2026-08-19
当前策略：轻量核心版
当前代码基线：本文件所在提交

## 1. 当前结论

入口、面板、基础布局、便签和天气已经形成可运行链路。项目停止扩展新卡片和平台能力，当前优先修复核心工作台的可用性、视觉密度和维护债务。

WorkspacePanel 已完成“静谧画布”视觉收口：减少多层边框和外围留白，把标题、日期、搜索与全局操作合并为单行头部，并统一缩小字号、间距、控件和网格尺度。面板使用系统 Desktop Acrylic、强调色微光和圆角，卡片使用更实的系统语义表面与轻量阴影；便签默认只保留标题、正文、新建和更多操作，Markdown、复制、删除及恢复命令按状态渐进呈现。面板、拖动回弹及渐进浮层采用克制且可反向的动效，不改变布局、便签、天气或 IPC 数据合同。

## 2. 核心五项

| 能力 | 当前状态 | 仍需处理 |
| --- | --- | --- |
| 任务栏入口 | 已实现公开 Win32 几何、点击、穿透和面板交接 | 发布前补完整显示矩阵和性能 |
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

- Release UnitTests：306/306；
- CoreBroker Release x64 构建：0 警告、0 错误；
- `WinWidgetBoard.CoreBroker.exe --pipe-handshake-smoke-test` 退出码 0；
- 真实桌面 `Test-CardDragInteraction.ps1 -WithBroker` 通过（`REAL-DRAG-PASS`）：四种卡片的拖动柄、卡面、交互控件隔离和 `Esc` 取消经真实鼠标验证，握手、`cards.subscribe`、面板可见性上报和 `layout.save` 全部经真实命名管道走重构后的分发路径；
- 真实桌面天气设置流程达到 `REAL-WEATHER-SETTINGS-PASS`：`weather.settings.save` 经真实面板写入 SQLite 并触发 provider 运行时重载；该脚本随后的重启复核段因桌面根元素遍历抛 `RPC_E_SERVERFAULT` 未完成，同一故障在改动前的基线构建上复现；
- 验收使用隔离临时数据和独立实例身份，结束后已清理。

这属于 L2 针对性证据，不替代发布候选的完整显示器、偏好和无障碍矩阵。更早轮次的完成证据以 Git 提交和测试名称为准。

## 5. 当前技术债务

1. `MainWindow.xaml.cs` 仍同时协调便签删除/编辑、拖动和设置；卡片订阅、布局持久化、便签列表和动效已提取。
2. `CoreBrokerCommandRouter.cs` 只保留 `session.ping`、方法分发和连接释放；便签、布局、天气设置、卡片订阅和面板可见性各自成为领域 handler，共用同一把锁与同一份操作缓存上限。
3. `ProviderRefreshScheduler.cs` 单文件状态机过大。
4. UIA 公共窗口、元素、输入和进程辅助函数已提取到 `scripts/WinWidgetBoard.UiAutomation.psm1`。当前参考机上 `Test-WeatherSettingsInteraction.ps1` 会在按名称遍历桌面根元素时抛出 `RPC_E_SERVERFAULT`；该故障在改动前的基线构建上同样复现，属于脚本对桌面根遍历的健壮性问题，不是天气链路缺陷。
5. Windows App SDK 自包含输出较大，开发构建不应长期留在仓库。
6. 干净网络 restore、CI、完整显示/无障碍/性能和发布矩阵尚未完成。
7. 计时器、待办和日历仍以延期占位卡保留；是否从默认工作区移除需要单独产品决定。

## 6. 当前工作

本轮已完成：

- 将 `CoreBrokerCommandRouter` 拆为领域 handler：`LayoutCommandHandler`、`WeatherSettingsCommandHandler`、`CardSubscriptionCommandHandler` 和 `PanelVisibilityCommandHandler`，与既有 `NoteCommandHandler` 对齐；
- 路由器只保留 `session.ping`、按 Contract `Methods` 分发和连接释放，可用性标志与面板可见性状态改为向对应 handler 转发；
- 全部 handler 继续共用路由器的同一把锁，域间序列化行为不变；操作缓存上限统一由 `CoreBrokerCommandSupport.MaxCachedOperations` 提供；
- 公开构造签名、`Handle` 重载、`RemoveConnection` 和四个可用性标志保持不变，IPC 方法、限制和错误码未改动。

## 7. 下一步

按单一目的拆分：

1. 由真实使用反馈决定是否从默认工作区移除延期占位卡；
2. 在一个参考 Windows 11 x64 环境测量入口到面板和后台占用；
3. 修复 `Test-WeatherSettingsInteraction.ps1` 对桌面根元素遍历的健壮性；
4. 仅修复核心五项的可复现问题。

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
