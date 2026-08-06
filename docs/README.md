# 文档索引

| 文档 | 目的 | 主要读者 |
| --- | --- | --- |
| `01-product-requirements.md` | 产品范围、需求编号、验收标准 | 产品、开发、测试 |
| `02-ux-design-spec.md` | 面板、卡片、动效、无障碍规范 | 设计、前端、UI 测试 |
| `03-technical-architecture.md` | 进程、模块、IPC、存储和集成方案 | 架构、开发 |
| `04-open-source-reference-analysis.md` | 可参考项目、实现模式和许可证边界 | 架构、法务、开发 |
| `05-implementation-plan.md` | 里程碑、任务分解、风险和交付物 | 项目管理、实施模型 |
| `06-luna-execution-guide.md` | Luna 的工作顺序、限制和输出格式 | Luna 模型 |
| `07-api-contracts.md` | 卡片、插件、IPC、权限和状态契约 | 开发、插件作者 |
| `08-testing-strategy.md` | 测试矩阵、性能与验收方法 | 测试、开发 |
| `09-security-privacy.md` | 威胁模型和隐私控制 | 安全、开发 |
| `adr/` | 重要架构决策及其理由 | 全体 |
| `status/implementation-status.md` | 当前事实、已完成和下一步 | 全体 |

## 文档状态词

- **已批准**：可直接作为实现依据。
- **提案**：方向明确，但在编码前仍需确认。
- **待决策**：不得由实施者自行选择。
- **已废弃**：仅用于历史追踪。

修改需求时，应同时更新：

1. 对应需求文档；
2. 验收标准；
3. 测试策略；
4. 实施状态；
5. 必要时新增 ADR。
