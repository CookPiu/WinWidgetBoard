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
