# ADR-0005：SQLite 本地优先，剪贴板默认不持久化

状态：Accepted
日期：2026-08-06

## 背景

产品包含布局、便签、待办、日历、计时器、账号和剪贴板。需要事务、迁移、离线能力和隐私隔离。

候选：

1. 全部 JSON；
2. SQLite；
3. 云数据库；
4. Windows LocalSettings；
5. 每插件独立文件。

## 决策

- SQLite 保存用户内容和布局；
- Launcher 所需最小配置用原子 JSON；
- OAuth token/API key 使用 Credential Manager 或 DPAPI；
- 剪贴板正文默认不写 SQLite；
- 云同步是可选 Provider；
- 数据库迁移前备份；
- 导出默认排除敏感内容。

## 理由

- SQLite 支持事务和 schema；
- 本地离线；
- 数据关系明确；
- 可备份恢复；
- Launcher 无需加载数据库；
- 剪贴板风险最小化。

## 后果

正面：

- 用户无账号可完整使用；
- 崩溃恢复更可靠；
- 云 Provider 可后加。

负面：

- migration 需要严格测试；
- 数据库损坏恢复复杂；
- 导入导出需要版本化；
- SQLite 仍可能在内存/备份中包含敏感用户内容。

## 约束

- WAL；
- foreign keys；
- 单写入队列；
- migration transaction；
- 备份；
- 凭据引用而非正文；
- 诊断脱敏；
- 剪贴板持久化若未来加入必须重新 ADR。
