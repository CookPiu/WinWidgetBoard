# 实施状态

最后更新：2026-08-17
当前策略：轻量核心版
当前代码基线：`d2cd71f refactor(workspace-panel): extract note list coordination`

## 1. 当前结论

入口、面板、基础布局、便签和天气已经形成可运行链路。项目停止扩展新卡片和平台能力，进入仓库瘦身与可维护性收口阶段。

最近完成的 M2.3.10 包含天气位置双语 UI、`weather.settings.get/save`、SQLite schema v3、revision 冲突保护和运行时 Provider 替换。

## 2. 核心五项

| 能力 | 当前状态 | 仍需处理 |
| --- | --- | --- |
| 任务栏入口 | 已实现公开 Win32 几何、点击、穿透和面板交接 | 发布前补完整显示矩阵和性能 |
| 面板 | 已实现 WinUI 壳层、锚定动效、关闭和模态保护 | 拆分集中式 code-behind |
| 基础布局 | 已实现 2/4/6 列、拖动、持久化、恢复和撤销 | 只修缺陷，不扩展复杂编辑 |
| 便签 | 已实现 CRUD、搜索、Markdown、自动保存和安全删除 | 只维护核心旅程 |
| 天气 | 已实现 Open-Meteo、订阅、手动位置和重启恢复设置 | 自动定位和跨重启 payload 缓存延期 |

## 3. 已有基础设施

- 当前用户 Named Pipe、握手、心跳、重连和版本化 Envelope；
- CoreBroker SQLite migration、备份边界和 revision 保护；
- 卡片不可变快照、连接级订阅、有界背压和 UI dispatcher；
- 集中 Provider 调度、超时、退避、可见性暂停和 fatal fault 边界；
- 验收进程身份与临时数据库隔离；
- 双语资源和基础无障碍锚点。

这些能力进入维护状态，不再继续平台化。

## 4. 最近完成证据

提交 `6824cb9` 的完成证据：

- Debug/Release UnitTests：292/292；
- CoreBroker、WorkspacePanel 和 UnitTests Debug/Release x64 构建：0 警告、0 错误；
- 天气位置保存、天气卡刷新和 Broker 重启读取真实 UIA 通过；
- Open-Meteo 到 `cards.snapshot` 再到 WorkspacePanel 的真实链路回归通过；
- 验收使用隔离临时数据，结束后已清理。

这些是该提交的历史证据，不表示当前文档瘦身任务重新跑过全部矩阵。

## 5. 当前技术债务

1. `MainWindow.xaml.cs` 仍同时协调便签删除/编辑、动效、拖动和设置；卡片订阅、布局持久化和便签列表已提取。
2. `CoreBrokerCommandRouter.cs` 集中多个领域命令。
3. `ProviderRefreshScheduler.cs` 单文件状态机过大。
4. UIA 公共窗口、元素、输入和进程辅助函数已提取到 `scripts/WinWidgetBoard.UiAutomation.psm1`；Release x64 天气设置真实 UIA 回归已通过，重启设置通过 `weather.settings.get` 验证。
5. Windows App SDK 自包含输出较大，开发构建不应长期留在仓库。
6. 干净网络 restore、CI、完整显示/无障碍/性能和发布矩阵尚未完成。

## 6. 当前工作

本轮已完成：

- 删除可重建输出和当前锁文件不再引用的缓存；
- 压缩产品、架构、测试、状态和实施文档；
- 删除重复的历史工作包与过时执行指南；
- 建立轻量核心策略 ADR。
- 提取 UIA 脚本共享模块，统一窗口/元素查找、交互、焦点、进程启动和清理；本项未修改产品运行时代码行为。
- 修正天气设置 UIA 验收边界：重启后验证设置 IPC 值，不把延期的跨重启天气 payload 当作持久化证据；Release x64 真实流程通过。
- 提取 `CardSubscriptionLifecycleCoordinator`，集中卡片订阅初始化、刷新定时器、Broker 重连恢复和释放；`MainWindow` 保留 UI 状态采集与薄转发。
- 本轮 `WorkspacePanel` Release x64 构建通过，UnitTests 292/292 通过，天气设置真实 UIA 流程通过。
- 提取 `LayoutPersistenceCoordinator`，集中 `layout.get/save`、DTO 映射、revision 和布局恢复校验；保留编辑模式、撤销/重做与 UI 状态在 `MainWindow`。
- 本轮布局工作包的 UnitTests 为 295/295，WorkspacePanel Release x64 构建通过，真实布局保存、Broker/面板重启恢复和再次编辑流程通过。
- 提取 `NoteListCoordinator`，集中便签搜索/全量列表、查询一致性、结果打开、删除后刷新和首条便签恢复；删除确认与 UI 状态仍由 `MainWindow` 管理。
- 本轮便签列表工作包的 UnitTests 为 298/298，WorkspacePanel Release x64 构建通过，真实当前/列表便签删除流程通过。

## 7. 下一步

按单一目的拆分：

1. 继续从 `MainWindow.xaml.cs` 拆分动效协调职责；
2. 拆分 CoreBroker 命令 handler；
3. 在一个参考 Windows 11 x64 环境测量入口到面板和后台占用；
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
