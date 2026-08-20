# 源码目录

本文件只说明目录结构和每个项目的职责边界。进程模型、IPC、存储和复杂度预算以
[当前技术架构](../docs/03-technical-architecture.md) 为准；当前进度、债务和下一步以
[实施状态](../docs/status/implementation-status.md) 为准。历史实现过程保留在 Git 提交中，本文不再逐项累计。

```text
src/
├─ LauncherHost/       # C++/Win32 任务栏入口：几何、自绘、输入、子进程生命周期与最小原生 Broker 客户端
├─ WorkspacePanel/     # C#/.NET 10/WinUI 3 面板：布局、便签、天气与设置 UI
├─ CoreBroker.Client/  # C# 面向 UI 的高层 Named Pipe 客户端
├─ CoreBroker/         # C# Worker：SQLite、天气 Provider、集中调度与 IPC 服务端
└─ Contracts/          # 版本化 Envelope、DTO、限制与协议版本
```

## 项目职责

| 项目 | 工程文件 | 主要内容 |
| --- | --- | --- |
| `LauncherHost` | `WinWidgetBoard.LauncherHost.vcxproj` | 入口窗口、`EntryVisual.cpp` 自绘与动效、命中与穿透、`ChildProcessJob.cpp` 持有的面板与 Broker 进程、`CoreBrokerClient.cpp` 保活 |
| `WorkspacePanel` | `WinWidgetBoard.WorkspacePanel.csproj` | `Shell/`、`Layout/`、`Notes/`、`Runtime/`、`Motion/`、`Interaction/`、`Ipc/`、`Settings/`、`Styles/`、`Strings/` |
| `CoreBroker.Client` | `WinWidgetBoard.CoreBroker.Client.csproj` | 会话、便签、布局、卡片和天气设置的类型化客户端 |
| `CoreBroker` | `WinWidgetBoard.CoreBroker.csproj` | `Ipc/`、`Commands/`、`Persistence/`、`Providers/`、`Hosting/` |
| `Contracts` | `WinWidgetBoard.Contracts.csproj` | 协议 `1.0` 的帧、Envelope、方法 payload 和卡片快照合同 |

## 不可改变的边界

- `LauncherHost` 不引用 WinUI、SQLite、HTTP 或托管 UI 运行时，不注入或读取 Explorer 私有视觉树；
  入口放置与显示偏好由 `LauncherPreferences.cpp` 读写 `HKCU`，不经 CoreBroker；
- `WorkspacePanel` 不直接打开 SQLite，不手工拼装低层 Envelope，只经 `Ipc/` 的会话与类型化客户端访问 Broker；
- `CoreBroker` 不引用 WinUI，校验全部 IPC 输入，队列、重试、超时和释放均有上界；
- `MainWindow.xaml.cs` 和 `Providers/ProviderRefreshScheduler.cs` 属于已知债务，
  只允许先提取协调器，再增加行为；
- `Commands/CoreBrokerCommandRouter.cs` 只做方法分发，新增命令进入对应领域 handler；
- 新增可测试的 WorkspacePanel 类型必须保持 WinUI 无关，并手动加入单元测试项目的 `Compile Include` 列表；
- 用户可见文案必须同时写入 `Strings/en-US` 和 `Strings/zh-CN`；
- 可见 UI 变更遵循 [UI、视觉与动效规范](../docs/02-ux-design-spec.md)，复用 `Styles/WorkspaceVisualStyles.xaml` 的 token。

`PluginHost`、独立卡片 SDK、云同步进程和硬件服务不属于当前架构，也不在当前范围内新建。
