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
| [0020](0020-open-meteo-weather-provider.md) | Accepted | Open-Meteo 天气 Provider 与网络、缓存和隐私边界 |
| [0021](0021-weather-location-settings.md) | Accepted | 天气位置设置通过版本化本地 IPC 保存并运行时替换 Provider |
| [0022](0022-lightweight-core-strategy.md) | Accepted | 收缩为核心五项并采用风险分级工程门禁 |
| [0023](0023-embedded-taskbar-entry-strip.md) | Accepted | 入口改为嵌入任务栏条带的自适应信息条 |
| [0024](0024-background-provider-keepalive.md) | Accepted | 允许有界的低频后台刷新，入口可在面板关闭时显示天气 |
| [0025](0025-resident-workspace-panel.md) | Accepted | 面板关闭改为隐藏，进程常驻以消除冷启动 |
| [0026](0026-launcher-owned-process-tree.md) | Accepted | 启动器用 Job Object 持有整棵进程树并自启动 CoreBroker |
| [0027](0027-weather-location-search.md) | Accepted | 天气位置改为地点搜索，新增 Open-Meteo 地理编码端点 |
| [0028](0028-system-monitor-scope-and-sensor-tiers.md) | Accepted | 重新纳入硬件监控，读数分公开 API 层与传感器层 |
| [0029](0029-drop-the-bundled-sensor-driver.md) | Accepted | 不分发内核传感器驱动，温度类读数保持不可读 |
| [0030](0030-token-usage-card.md) | Accepted | 纳入 Token 用量卡片，按厂商分别解析本机会话记录且不落库 |
| [0031](0031-cpu-clock-from-per-core-counters.md) | Accepted | CPU 频率改由每核 PDH 计数器测量，脱离传感器层 |
| [0032](0032-windows-device-weather-location.md) | Accepted | 天气默认经 Windows 前台授权读取设备位置，手动位置保留为降级与覆盖 |
| [0033](0033-hwinfo-shared-memory-sensor-source.md) | Accepted | 温度与风扇改由 HWiNFO 共享内存读取，读不到时说明需运行 HWiNFO |
| [0034](0034-core-temp-as-a-second-sensor-source.md) | Accepted | 温度源改为多来源并逐项判断，新增无时限的 Core Temp |
