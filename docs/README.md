# 文档索引

当前文档只服务轻量核心版。已完成过程保留在 Git，不再要求开发者通读累计工作包。

| 文档 | 用途 |
| --- | --- |
| [01-product-requirements.md](01-product-requirements.md) | 当前核心五项范围和验收边界 |
| [02-ux-design-spec.md](02-ux-design-spec.md) | 面板、布局、便签、天气和动效规范 |
| [03-technical-architecture.md](03-technical-architecture.md) | 当前进程、IPC、存储和复杂度边界 |
| [05-implementation-plan.md](05-implementation-plan.md) | 精简后的近期计划 |
| [07-api-contracts.md](07-api-contracts.md) | 已实现的 IPC 与数据契约 |
| [08-testing-strategy.md](08-testing-strategy.md) | 风险分级验证 |
| [09-security-privacy.md](09-security-privacy.md) | 当前攻击面和隐私约束 |
| [status/implementation-status.md](status/implementation-status.md) | 当前事实、债务和下一步 |
| [history/completed-milestones.md](history/completed-milestones.md) | 已完成里程碑简表 |
| [adr/README.md](adr/README.md) | 高成本架构决策 |

## 维护规则

- 同一事实只保留一个权威位置。
- 普通功能或修复不创建工作包文档。
- 状态页不复制历史提交和逐次测试数字。
- ADR 只用于难回退的架构、持久化、安全、许可或产品范围决策。
- 延期功能不是当前验收项。
