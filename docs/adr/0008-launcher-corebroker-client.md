# ADR-0008：LauncherHost 使用最小原生 CoreBroker 客户端

状态：Accepted
日期：2026-08-07

## 背景

M2.0.2 已为 WorkspacePanel 提供可复用的 .NET Named Pipe 客户端，但 LauncherHost 是常驻的 C++/Win32 进程，不能为了 IPC 引入 WinUI、.NET UI 运行时、SQLite、HTTP 或第三方 JSON 库。技术架构仍要求 LauncherHost 与 CoreBroker 保持独立的 launcher pipe。

## 决策

- LauncherHost 使用独立的轻量 C++ 客户端发送版本化 `session.hello` 和 `session.ping`；
- 客户端复用相同的 4 字节 little-endian 长度帧、1MiB 上限和 Envelope 字段；
- 使用可取消的 IPC worker 和 Win32 overlapped Named Pipe I/O，连接和单次读写均有短超时，入口消息循环只负责启动 worker；
- 客户端从当前用户环境变量 `WINWIDGETBOARD_COREBROKER_SESSION_TOKEN` 读取令牌，缺少令牌时不连接，令牌和 payload 不写日志；
- 入口定时器只负责确保 worker 存在，worker 在连接冷却到期或心跳到期时执行实际 I/O，失败进入冷却重试，不影响入口和面板启动；
- 本阶段不在 LauncherHost 中实现卡片、布局、数据库或网络业务方法。

## 理由

- 保持 LauncherHost 的常驻依赖和进程职责边界；
- 与 CoreBroker 现有协议和 WorkspacePanel 客户端保持一致；
- 通过 overlapped I/O 和超时避免 Broker 故障冻结任务栏入口；
- 连接失败可以安全降级，后续业务仍由 WorkspacePanel/CoreBroker 负责。

## 后果

- LauncherHost 可以验证 CoreBroker 会话可用性并维持保活；
- 当前仍使用受控环境变量传递令牌，不代表正式安装包的令牌生命周期已经完成；
- C++ 客户端只实现本阶段的保活方法，真实业务方法和幂等命令仍由后续工作包定义；
- 提供 `--corebroker-smoke-test`，要求启动同一令牌的 CoreBroker 后进行真实进程间验证。

## 验证

- LauncherHost Debug/Release 构建无警告；
- 设置 `WINWIDGETBOARD_COREBROKER_SESSION_TOKEN` 并启动同令牌 CoreBroker 后，`--corebroker-smoke-test` 返回 0；
- 未设置令牌时 smoke 返回专用失败码，不输出令牌；
- Broker 不可用时正常 LauncherHost 仍可启动，入口定时器不留下线程或进程。
