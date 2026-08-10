# WinWidgetBoard

> 工作名称，后续可重命名。

WinWidgetBoard 是一个面向 Windows 11 的本地优先快捷工作台。用户关闭系统自带 Widgets 入口后，可通过任务栏左下角的自定义按钮打开半屏卡片面板，在其中排列天气、便签、日历、计时器、待办、剪贴板和系统监控等卡片。

本仓库当前处于 **M2.2 网格布局阶段**。M1.0 已建立最小解决方案并锁定工具链；M1.1 已加入不注入 Explorer 的 LauncherHost 入口 POC；M1.2 已加入 WorkspacePanel 面板壳层和确定性几何计算；M1.2.1 已加入最小同用户进程启动交接；M1.3.1 已加入单张假卡片拖动输入 POC；M1.3.2 和 M1.3.3 已通过当前 Windows 11 会话验收；M2.0.1 已完成版本化 IPC Envelope、JSON Schema 和长度帧校验；M2.0.2 已实现当前用户 Named Pipe、共享客户端、WorkspacePanel 会话和 LauncherHost 原生保活 POC；M2.0.3 已实现面板可见性业务上报、有限期幂等去重和 Broker 重启后的安全重报；M2.1.1 已完成 SQLite schema v1、事务化迁移和迁移前备份基础；M2.1.2 已补充参数化数据访问、事务接线和恢复故障注入；M2.1.3 已完成 NTE-001 便签持久化仓储和 revision 冲突保护；M2.1.4 已完成便签自动保存服务层；M2.1.5 已完成便签 CoreBroker IPC；M2.1.6 已接入 WorkspacePanel 单便签编辑器和高层客户端。完整显示器矩阵、定量性能门禁、便签完整能力、领域数据库 CRUD、剩余布局/卡片业务和完整产品功能仍未完成验收。

M2.2.1 已完成响应式网格纯函数、尺寸映射和确定性无重叠排布测试，M2.2.2 已加入可测试的卡片布局 ViewModel，M2.2.3 已接入 ItemsRepeater 卡片视觉排布，M2.2.4 已加入显式编辑模式门禁，M2.2.5 已加入确定性拖动落点投影，M2.2.6 已接入指针拖动和释放提交，M2.2.7 已修复拖动边界和视觉变换回收问题，M2.2.8 已接入 CoreBroker/SQLite 布局持久化，M2.2.9 已修复便签状态覆盖和逻辑网格位置重放，M2.2.10 已统一四种卡片的拖动入口并接入占用单元格的实时让位预览，M2.2.11 已恢复异常大的持久化空行，并修复异步加载后的模板身份和提交后网格重排；让位弹簧补间、虚拟化回收和撤销/重做仍在后续工作包中。

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
