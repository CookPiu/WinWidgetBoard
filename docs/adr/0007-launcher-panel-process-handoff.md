# ADR-0007：LauncherHost 通过受控进程启动 WorkspacePanel

状态：Accepted
日期：2026-08-06

## 背景

M1.1 的 LauncherHost 入口已经可以识别用户点击，但此前只记录 Toggle 请求，没有启动面板。M1.2 的 WorkspacePanel 已能接收显示器、工作区、入口矩形和 DPI 启动上下文，下一步需要把两者连接起来。

当前仍未实现 CoreBroker、IPC、数据库或安装包配置，因此不能把面板启动交接误认为完整产品协议。

## 决策

M1.2.1 采用最小的同用户进程交接：

- LauncherHost 使用公开 `CreateProcessW` 启动与安装目录相邻的 `WinWidgetBoard.WorkspacePanel.exe`；
- 启动参数携带经过 LauncherHost 重新读取和校验的 monitor、work-area、launcher 和 DPI 矩形；
- 面板已经运行时，LauncherHost 只激活其顶层窗口；关闭时发送公开 `WM_CLOSE`，不强制终止面板；
- LauncherHost 通过进程句柄轮询回收状态，面板自行退出后入口状态恢复；
- 开发构建可通过当前用户环境变量 `WINWIDGETBOARD_WORKSPACE_PANEL` 指定面板路径；生产路径仍要求同目录部署；
- 提供 `--panel-launch-smoke-test`，用面板自身 `--smoke-test` 验证真实进程创建、启动上下文传递和退出码。
- 提供 `--panel-lifecycle-smoke-test`，验证正常面板窗口创建、公开 `WM_CLOSE` 和干净进程退出；该测试不把非交互会话中的前台激活失败当作通过或失败依据。

## 明确不做

- 不引入命名管道、JSON Envelope、CoreBroker 或持久化；
- 不向 Explorer 注入、不发送私有 Shell 消息、不使用窗口钩子；
- 不使用 `TerminateProcess` 结束正常运行的面板；仅在交接 smoke 超时后清理本次 smoke 子进程；
- 不把环境变量路径作为用户可配置的永久设置或安全沙箱；它仅用于本地开发验证。

## 理由

- 进程职责仍符合 ADR-0002，LauncherHost 不加载 WinUI、SQLite 或网络运行时；
- CLI 上下文与 M1.2 已有解析器复用，未来可由 IPC 握手替换而不改变几何层；
- 关闭使用窗口消息，允许 WorkspacePanel 执行自己的清理；
- 同目录部署与安装包布局一致，开发覆盖路径不会污染产品配置。

## 后果

正面：

- 入口点击可以形成真实面板启动链路；
- LauncherHost 可发现面板退出并恢复按钮状态；
- 可在没有 CoreBroker 的情况下验证冷启动和进程边界。

负面：

- CLI 参数不是长期 IPC 契约，缺少握手、重连和版本协商；
- 当前面板关闭后重新打开仍可能是冷启动；
- 启动路径缺失时只能安全记录错误，不能自动修复安装；
- 外部点击、输入保护和性能目标仍需真实桌面验收。

## 验证

- LauncherHost Debug/Release 构建无警告；
- `--panel-launch-smoke-test` 在 `WINWIDGETBOARD_WORKSPACE_PANEL` 指向 Release 面板时返回 0；
- `--panel-lifecycle-smoke-test` 在 Debug/Release 组合下返回 0；
- 负坐标、多 DPI 启动上下文由 WorkspacePanel 单元测试和 smoke 覆盖；
- 真实桌面仍需验证点击、失焦关闭、重复打开、Explorer 重启、多显示器和全屏行为。
