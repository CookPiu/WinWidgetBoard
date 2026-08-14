# ADR-0018：Provider 生产宿主与卡片快照边界

状态：Accepted
日期：2026-08-14

## 背景

M2.3.5 已建立 CoreBroker 内的 Scheduled Provider 调度核心，但调度核心仍由
测试直接调用 `PumpDueAsync`，Provider 结果也尚未拥有稳定的卡片快照适配边界。
API 文档已经描述 `cards.subscribe`，而代码还没有把 Provider 结果、卡片序列和
事件队列收敛到共享 Contracts。若此时直接接入真实天气 HTTP，会同时扩大网络权限、
缓存、隐私和 Provider 选择范围。

## 决策

1. CoreBroker 由唯一的 `ProviderRefreshHost` 持有生产 pump。它只驱动
   `ProviderRefreshScheduler.PumpDueAsync`，使用同一个单调时钟进行有界等待；Provider
   不得创建独立的永久 Timer 或 UI DispatcherTimer。
2. 调度器将 `OutOfMemoryException`、`StackOverflowException` 和
   `AccessViolationException` 等进程级不可恢复异常交给
   `ProviderProcessFatalSupervisor`。库层不把它们归一为普通 Provider 失败；宿主提供
   进程终止策略，当前 CoreBroker 记录类型、取消服务并返回专用非零退出码。
3. Provider 结果通过 `ProviderCardSnapshotAdapter` 转换为 Contracts 中的
   `CardStateSnapshot`。每个卡片实例独立维护 sequence，状态、freshness、诊断码和
   allowed actions 必须显式映射，不从 payload 缺失字段推断权限。
4. `CardSnapshotEventBuffer` 按 instance ID 合并待发送状态；达到有界容量时标记
   overflow，后续 `cards.subscribe` 传输层必须断开慢客户端并记录诊断，不得静默丢弃
   不同实例的状态事件。
5. 本 ADR 只冻结共享合同、宿主和适配器边界，不在本包接通 `cards.subscribe` 的长连接
   事件传输，也不新增真实 HTTP、缓存、系统网络/电源事件或天气数据源。协议版本继续
   使用 1.0，直到传输能力被单独接入并完成兼容测试。

## 后果

- Provider 调度有明确的生产生命周期和可测试的 fatal fault 观察点；
- CoreBroker 与 WorkspacePanel 通过 Contracts 共享快照形状，CoreBroker 不引用 WinUI；
- 结果缓冲不会形成无界内存队列；
- `cards.subscribe` 的实时传输、WorkspacePanel UI dispatcher 接入和真实 Provider 仍
  需要后续独立工作包；当前不能宣称真实 Provider 或完整 CRD-001～005 已完成。

## 被否决方案

- **每张卡片自行创建 Timer**：会破坏统一退避、可见性节能和全局资源预算；
- **在 CoreBroker 直接引用 WorkspacePanel Runtime**：会跨越进程边界并使 Provider 难以测试；
- **将进程级异常转换为 `provider.failed`**：可能继续运行已不可靠的宿主，掩盖不可恢复故障；
- **现在直接接入天气 HTTP**：首发数据源、网络权限、缓存和隐私范围尚未完成用户决策。
