# WinWidgetBoard

> 工作名称，后续可重命名。

WinWidgetBoard 是一个面向 Windows 11 的本地优先快捷工作台。用户关闭系统自带 Widgets 入口后，可通过任务栏左下角的自定义按钮打开半屏卡片面板，在其中排列天气、便签、日历、计时器、待办、剪贴板和系统监控等卡片。

本仓库当前处于 **需求与架构基线阶段**，尚未进入功能实现。文档以便后续由 Luna 模型或开发人员按阶段实施为目标。

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

## 当前状态

请以 [实施状态](docs/status/implementation-status.md) 为唯一进度入口。文档中标记为“待决策”的事项不得由实施模型静默假设。

## 许可证状态

项目许可证尚未决定。在确定许可证前：

- 不复制第三方项目源码；
- 只允许根据公开行为和架构重新实现；
- 引入任何依赖前必须记录其许可证、用途和分发影响；
- GPL、AGPL、Anti-996 或带额外限制的代码不得进入代码库。
