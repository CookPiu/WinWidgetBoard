# 架构决策记录

ADR 一旦批准不应被直接重写结论。若决策改变：

1. 新增 ADR；
2. 标记旧 ADR 被替代；
3. 解释迁移和兼容影响；
4. 更新需求、架构、测试和实施状态。

| ADR | 状态 | 决策 |
| --- | --- | --- |
| [0001](0001-stable-taskbar-overlay.md) | Accepted | 稳定版使用独立覆盖入口 |
| [0002](0002-split-process-architecture.md) | Accepted | 原生入口与 WinUI 面板分进程 |
| [0003](0003-responsive-card-grid.md) | Accepted | 使用确定性响应式网格 |
| [0004](0004-plugin-security-boundary.md) | Accepted | MVP 不开放第三方插件，后续需要真实沙箱 |
| [0005](0005-local-first-storage.md) | Accepted | SQLite 本地优先，剪贴板默认不持久化 |
| [0006](0006-source-reuse-and-license.md) | Accepted | 许可证未定前只重写，不复制参考源码 |
| [0007](0007-launcher-panel-process-handoff.md) | Accepted | LauncherHost 通过受控同用户进程启动 WorkspacePanel |
| [0008](0008-launcher-corebroker-client.md) | Accepted | LauncherHost 使用最小原生 CoreBroker 客户端 |
| [0009](0009-panel-visibility-idempotency.md) | Accepted | 首个业务方法采用面板可见性状态上报和幂等重试 |
| [0010](0010-note-repository-and-revision.md) | Accepted | 便签仓储使用规范化更新时间作为并发令牌 |
| [0011](0011-note-autosave-failure-policy.md) | Accepted | 便签自动保存最新输入优先，失败保留内存草稿 |
| [0012](0012-note-ipc-boundary.md) | Accepted | 便签通过 CoreBroker 当前用户 IPC 暴露 |
| [0013](0013-note-panel-client-and-editor.md) | Accepted | WorkspacePanel 通过高层客户端和可测试 ViewModel 接入便签 |
| [0014](0014-logical-layout-cell-replay.md) | Accepted | 持久化逻辑网格行列以恢复用户布局空位 |
| [0015](0015-bounded-layout-gap-recovery.md) | Accepted | 仅在内存中恢复异常大的持久化布局空行 |
| [0016](0016-note-delete-edit-boundary.md) | Accepted | 便签删除复用 revision 并以确认和草稿保护隔离 |
| [0017](0017-acceptance-process-and-data-isolation.md) | Accepted | 验收进程使用独立身份和临时数据目录 |
| [0018](0018-provider-host-and-card-snapshot-boundary.md) | Accepted | Provider 生产 pump、卡片快照适配与 fatal fault 边界 |
| [0019](0019-cards-subscription-event-transport.md) | Accepted | cards.subscribe 连接级事件传输与有界背压 |
