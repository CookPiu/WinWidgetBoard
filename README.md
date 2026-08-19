# WinWidgetBoard

WinWidgetBoard 是一个面向 Windows 11 的本地优先快捷工作台。它通过独立任务栏入口打开原生半屏面板，不注入或修改 `explorer.exe`。

项目当前采用“轻量核心版”策略：做少量高频能力，把启动速度、后台占用、可靠性和可维护性放在功能数量之前。

## 核心范围

| 能力 | 当前方向 |
| --- | --- |
| 任务栏入口 | 原生 C++/Win32 独立覆盖窗口 |
| 面板 | C#、.NET 10、WinUI 3，按需启动 |
| 基础布局 | 响应式网格、拖动、持久化和已有撤销能力 |
| 便签 | 本地 CRUD、搜索、Markdown、自动保存 |
| 天气 | Open-Meteo 当前天气、手动位置、离线状态 |

以下能力不属于当前轻量版：计时器、待办、剪贴板、日历、系统监控、第三方插件、账号、云同步、自动定位、复杂诊断平台和企业级发布体系。仓库中已有的占位或基础合同不代表继续实施承诺。

## 当前状态

核心进程、IPC、SQLite、布局、便签、卡片快照订阅、Open-Meteo 和天气位置设置已经形成可运行链路。最近完成的天气设置链路包含双语 UI、revision 保护的本地持久化和运行时 Provider 切换。

当前不继续扩展卡片品类，优先处理：

1. 拆分 `MainWindow.xaml.cs`、命令路由和调度器等集中式大文件；
2. 在有构建产物的参考环境验证共享 UIA 模块；
3. 保持构建输出和 NuGet 缓存可控；
4. 在一个明确参考环境中验证核心五项体验。

准确状态见 [实施状态](docs/status/implementation-status.md)。

## 进程边界

| 组件 | 职责 | 生命周期 |
| --- | --- | --- |
| `LauncherHost` | 任务栏入口、几何、点击和面板启动 | 常驻、最小依赖 |
| `WorkspacePanel` | WinUI 面板、布局、便签和天气视图 | 按需 |
| `CoreBroker` | SQLite、天气 Provider、调度和 IPC | 按启用能力运行 |
| `CoreBroker.Client` | 面板使用的高层 Named Pipe 客户端 | 随面板 |
| `Contracts` | 版本化 JSON 协议 | 共享库 |

安全边界保持不变：不注入 Explorer，不让面板直接访问数据库，不让常驻入口加载 UI 或网络框架。

## 工程策略

- 小改动只运行相关测试和受影响构建。
- UI/IPC/数据库变更增加一条针对性真实流程。
- 完整 Debug/Release、显示矩阵、性能和发布门禁只在发布候选阶段执行。
- 普通功能不再创建独立工作包文档。
- 构建输出只保存在被忽略的目录中，并在验证结束后清理。

详细规则见 [AGENTS.md](AGENTS.md) 和 [测试策略](docs/08-testing-strategy.md)。

## 文档

建议只按任务需要阅读：

1. [轻量产品需求](docs/01-product-requirements.md)
2. [UI、视觉与动效规范](docs/02-ux-design-spec.md)
3. [UI 变更模板](docs/templates/ui-change-template.md)
4. [当前技术架构](docs/03-technical-architecture.md)
5. [当前实施计划](docs/05-implementation-plan.md)
6. [当前接口契约](docs/07-api-contracts.md)
7. [风险分级测试策略](docs/08-testing-strategy.md)
8. [安全与隐私边界](docs/09-security-privacy.md)
9. [ADR 索引](docs/adr/README.md)

工具链和构建命令见 [开发工具链](docs/development/toolchain.md)。

## 许可证

项目许可证尚未决定。确定前不得复制第三方源码或资源；新增依赖必须记录用途、版本和许可证。
