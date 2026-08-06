# ADR-0002：原生入口、WinUI 面板和 Broker 分进程

状态：Accepted
日期：2026-08-06

## 背景

入口要求极低常驻资源；半屏面板要求丰富控件、文字输入、无障碍、Mica 和高质量动效；计时器与通知又需要脱离面板运行。

候选：

1. 单一 WinUI 3 进程常驻；
2. 单一 C++ 自绘应用；
3. C++ Launcher＋按需 WinUI Panel＋Broker；
4. Electron/Tauri 单进程。

## 决策

采用：

- `LauncherHost.exe`：C++/Win32，最小常驻；
- `WorkspacePanel.exe`：C#/.NET 10/WinUI 3，按需；
- `CoreBroker.exe`：C# Worker，数据与后台任务；
- `PluginHost.exe`：后续外部扩展。

## 理由

- Launcher 不加载 UI 框架；
- Panel 可以使用原生 WinUI 控件和 UI Automation；
- Panel 可退出释放资源；
- Broker 保持计时器和通知；
- 故障边界清晰；
- 每个进程职责可独立测试。

## 后果

正面：

- 后台更轻；
- UI 和数据分离；
- 面板崩溃可恢复；
- 插件未来可隔离。

负面：

- IPC 和版本管理复杂；
- 冷启动需要优化；
- 安装包包含多个进程；
- 调试跨进程状态更难。

## 约束

- Launcher 不引用 WinUI、SQLite、HTTP；
- Panel 不直接访问数据库和敏感 API；
- Broker 不拥有窗口；
- 所有跨进程数据版本化；
- 不为了减少 IPC 把业务逻辑重新塞回 UI。

## 验证

- Panel 冷/热启动；
- Broker 重启；
- Panel 崩溃重启；
- Launcher 空闲资源；
- IPC 断开和重连；
- 面板退出后无 UI 渲染。
