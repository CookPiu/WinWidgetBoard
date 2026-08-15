# ADR-0019：cards.subscribe 连接级事件传输与背压

状态：Accepted
日期：2026-08-15

## 背景

M2.3.6 已冻结 `CardStateSnapshot`、Provider 适配器和按实例合并的有界缓冲，
但 CoreBroker 仍没有把 `cards.subscribe` 接入 Named Pipe 长连接。若继续让客户端
按一次请求读取一次响应，异步 `cards.snapshot` 事件会被误当成命令响应，或者在管道中
无人读取。UI 还需要一个不依赖 WinUI 的运行时调度边界，以保证快照不会从后台读线程直接
触碰界面对象。

## 决策

1. CoreBroker 为每个已握手连接维护至多一个 `cards.subscribe` 订阅。新的有效订阅替换
   旧订阅；订阅 ID 和当前快照缓存均限定在该连接范围内。`instanceIds` 和
   `visibleInstanceIds` 均去重并限制在合同上限，后者必须是前者的子集。
2. 订阅只在 `panelVisible=true` 且实例可见时接收事件；订阅请求可返回匹配的最新初始
   快照。面板隐藏时不向连接发送卡片事件，重新可见时由客户端重新提交订阅视图。
3. `cards.snapshot` 使用 `Event` Envelope 独立发送。服务端命令响应和事件共用一个有序
   写锁，因此响应不会被事件队列静默丢弃；客户端读取泵按 `MessageType` 和 correlation ID
   将事件与命令响应分流。
4. 事件队列按实例合并且有界，生产默认容量为 32；用于新订阅初始响应的最新快照缓存
   默认最多保留 1024 个实例。溢出表示客户端无法证明已收到完整状态流，CoreBroker
   取消该连接并要求客户端重新握手、重新订阅；不得丢弃后续不同实例的状态而继续保持
   连接。溢出诊断写入 CoreBroker 标准错误输出。
5. `CoreBrokerCardsClient` 只负责订阅请求和事件解析；WorkspacePanel 使用注入的
   `CardSnapshotDispatcher` 将共享快照映射为 `CardRuntimeSnapshot`，严格检查实例、类型、
   schema、时间和 sequence。该适配器不直接引用 WinUI，实际 DispatcherQueue 由上层注入。
6. 本 ADR 只接通内存快照到本地 IPC 的实时传输，不选择天气数据源，不接入真实 HTTP、
   DNS/TLS、缓存、OS 网络/电源事件或第三方 Provider。连接断开后，调用方必须重新提交
   `cards.subscribe`，不把旧订阅状态跨连接持久化。

## 后果

- WorkspacePanel 可以在同一 Named Pipe 连接上同时发送命令并接收快照事件；
- 慢客户端的内存占用有明确上限，溢出通过断连接保证状态完整性；
- CoreBroker 不引用 WorkspacePanel 或 WinUI，运行时线程切换责任位于 dispatcher 适配层；
- 当前仍不能宣称真实 Provider、HTTP、缓存或端到端真实天气刷新已经完成。

## 被否决方案

- **为每次快照新建独立 Named Pipe**：破坏连接级握手和顺序，增加句柄与重连复杂度；
- **让请求调用方直接读取第二帧**：无法区分异步事件和响应，事件到达时会造成请求错配；
- **无限增长事件队列**：慢客户端会把 Provider 结果压力转化为 CoreBroker 内存风险；
- **静默丢弃最旧事件**：WorkspacePanel 无法证明状态连续性，可能长期显示错误快照；
- **在 CoreBroker 直接调用 WinUI DispatcherQueue**：跨越进程边界并破坏 CoreBroker 可测试性。
