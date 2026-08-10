# ADR-0009：首个业务方法采用面板可见性状态上报

状态：Accepted
日期：2026-08-07

## 背景

M2.0.2 已完成 Named Pipe 握手、心跳和断线重连，但 CoreBroker 还没有真实业务方法。直接进入布局、卡片或数据库会同时扩大 IPC、持久化和 UI 范围，难以单独验收重连后的业务恢复。

## 决策

- 首个业务方法定义为 `panel.report-visibility`，关联 PNL-004、G-006 和 NFR-REL-001；
- 请求使用 `clientOperationId`、`panelVisible` 和可选 `displayId`；这是状态设置，不使用 toggle 语义；
- CoreBroker 在进程内缓存有限数量的 operation 结果。相同 ID 和 payload 返回相同业务结果，不重复应用状态；同一 ID 搭配不同 payload 拒绝；
- WorkspacePanel 保存最新目标可见性。心跳发现 Broker 重连后，使用新的 operation ID 重新上报该状态；
- 本阶段不引入数据库、不持久化面板可见性、不实现布局/卡片/插件业务，也不让 LauncherHost 承担该业务方法；
- Broker 重启后的恢复保证是安全重新设置目标状态，不承诺跨进程保留内存 revision 或 operation 缓存。

## 理由

- `show/hide` 状态设置天然可重复，避免网络重试把一次 toggle 变成两次相反操作；
- operation ID 仍能覆盖响应丢失时的重复提交，且不会提前引入 M2.1 存储迁移；
- WorkspacePanel 已拥有会话服务和心跳，恢复逻辑可以留在可测试的服务层，不写入 XAML code-behind；
- 该方法能用单元/集成测试和真实 WinUI 进程 smoke 独立验收。

## 后果

- CoreBroker 增加一个受限业务路由和有界内存去重表；
- 当前 Broker 重启后 revision 从新进程重新计数，调用方不得把 revision 当作跨重启持久游标；
- 真实布局、卡片实例和数据库命令仍需后续工作包与独立 ADR；
- LauncherHost 继续只使用 `session.hello`/`session.ping`，不读取面板或卡片数据。

## 验证

- `IT-PIPE-008` 覆盖重复 operation 返回相同 revision、冲突 payload 拒绝且只应用一次；
- `IT-PIPE-009` 覆盖 WorkspacePanel Broker 重启后重新上报可见性；
- WorkspacePanel `--broker-smoke-test` 在真实 CoreBroker 进程和同一会话令牌下返回 0；
- Debug/Release UnitTests、WorkspacePanel 构建和 UI smoke 均通过。
