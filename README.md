# WinWidgetBoard

> 工作名称，后续可重命名。

WinWidgetBoard 是一个面向 Windows 11 的本地优先快捷工作台。用户关闭系统自带 Widgets 入口后，可通过任务栏左下角的自定义按钮打开半屏卡片面板，在其中排列天气、便签、日历、计时器、待办、剪贴板和系统监控等卡片。

本仓库当前处于 **M2.3 统一卡片运行时顺序实施阶段**。M1.0 已建立最小解决方案并锁定工具链；M1.1～M1.3 已完成入口、面板和基础交互 POC；M2.0 已建立版本化 IPC、当前用户 Named Pipe 和可恢复面板可见性上报；M2.1 已建立 SQLite、仓储、便签 IPC、自动保存和 WorkspacePanel 编辑器。M2.2.13 / M2.4.11 已完成当前单显示器真实桌面验收、验收进程/数据隔离和生产数据只读复核；M2.3.0～M2.3.2 已完成 UI 基础层、统一卡片定义/实例/生命周期/不可变快照、实例级错误边界，以及面板与 ItemsRepeater 实现视口驱动的 Hidden/Visible 和新鲜快照调度。M1.0 干净环境/CI、真实多显示器/DPI/无障碍/性能矩阵、真实 Provider 周期/退避调度、便签版本历史、其他领域 CRUD、剩余卡片业务和完整产品功能仍未完成验收。

M2.2 已具备响应式 2/4/6 列、确定性排布、ItemsRepeater 视口实现、显式编辑、四卡拖动/让位、布局持久化/重放和 20 步撤销重做；M2.3 已将现有四卡接入稳定类型目录和独立运行时实例，未知状态安全降级，单卡快照刷新异常不会污染其他实例，并以集中式事件调度暂停 Hidden 卡片的纯 UI 快照工作、在 Visible 时请求当前本地快照；M2.4 已具备便签复制、保存重试、搜索/安全打开、撤销重做、Markdown 往返、多便签列表、新建和删除。当前桌面已通过四卡拖动、布局历史、布局重启恢复和 5 条便签 UIA 链路。设置草稿、真实 Provider 周期/退避、让位弹簧补间、完整无障碍与硬件性能矩阵仍在后续工作包中。

验收工具只在显式 `--acceptance-test` 下使用独立 GUID 和系统临时目录；生产单实例、数据路径与失焦关闭语义保持不变。具体证据和未完成边界见 [桌面验收与隔离收口工作包](docs/work-packages/M2.2.13-M2.4.11-desktop-acceptance-closeout.md)。

## 核心定位

- 体验级替代 Windows 11 Widgets，不注入、不修改、不接管系统 Widgets 进程。
- 无新闻流、无广告、无强制账号，本地功能无需联网。
- 任务栏只保留一个入口，复杂内容集中到半屏工作台。
- 卡片采用可拖拽、可跨列、自动避让的响应式网格。
- 后台低占用，面板关闭后停止不可见动画并降低数据刷新频率。
- 内置卡片使用原生 WinUI 3；第三方插件默认在独立进程运行。

## 已确定的技术方向

| 组件 | 技术方向 | 生命周期 |
| --- | --- | --- |
| `LauncherHost` | C++/Win32、DirectComposition | 常驻 |
| `WorkspacePanel` | C#、.NET 10、WinUI 3 | 按需启动、短时保温 |
| `CoreBroker.Client` | C#、.NET 10、Named Pipe 客户端 | 随客户端进程 |
| `CoreBroker` | C#、.NET 10 Worker | 按功能常驻 |
| `PluginHost` | 独立受控进程 | 按插件启动 |
| `Contracts` | 版本化 JSON 协议 | 共享 |

稳定版本明确禁止使用 Explorer 注入和未公开的任务栏 XAML 视觉树。任务栏按钮通过独立覆盖窗口实现。

## 文档阅读顺序

1. [产品需求文档](docs/01-product-requirements.md)
2. [UX 与视觉交互规范](docs/02-ux-design-spec.md)
3. [技术架构](docs/03-technical-architecture.md)
4. [开源项目参考分析](docs/04-open-source-reference-analysis.md)
5. [实施计划](docs/05-implementation-plan.md)
6. [Luna 执行指南](docs/06-luna-execution-guide.md)
7. [接口与数据契约](docs/07-api-contracts.md)
8. [测试与验收策略](docs/08-testing-strategy.md)
9. [安全与隐私威胁模型](docs/09-security-privacy.md)
10. [架构决策记录](docs/adr/README.md)

## 开发构建

工具链前置条件、锁定版本和构建命令见 [M1.0 工具链基线](docs/development/toolchain.md)。项目当前只验证 Debug/Release x64；这不代表正式产品的平台范围已经决定。

## 当前状态

请以 [实施状态](docs/status/implementation-status.md) 为唯一进度入口。文档中标记为“待决策”的事项不得由实施模型静默假设。

## 许可证状态

项目许可证尚未决定。在确定许可证前：

- 不复制第三方项目源码；
- 只允许根据公开行为和架构重新实现；
- 引入任何依赖前必须记录其许可证、用途和分发影响；
- GPL、AGPL、Anti-996 或带额外限制的代码不得进入代码库。
