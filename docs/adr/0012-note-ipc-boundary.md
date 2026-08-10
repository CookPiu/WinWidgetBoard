# ADR-0012：便签通过 CoreBroker 当前用户 IPC 暴露

状态：Accepted
日期：2026-08-08

## 背景

M2.1.3/M2.1.4 已完成便签仓储和自动保存服务，但 WorkspacePanel 不应直接访问 SQLite。需要把便签读写接入现有当前用户命名管道，同时保留输入校验、revision 冲突和重试安全边界。

## 决策

- CoreBroker 提供 `notes.save`、`notes.get`、`notes.search` 和 `notes.delete` 四个 IPC 方法；
- `notes.save` 创建时不带 revision，更新时必须带 `expectedUpdatedAtUtc`；删除始终需要 revision；
- 写操作必须带 `clientOperationId`，CoreBroker 在有界缓存内对相同 ID 和相同 payload 返回相同结果；相同 ID 搭配不同 payload 拒绝；
- IPC 层限制便签 ID、标题、正文、时间戳和搜索词长度，并只接受 `plain-text`/`markdown`；
- CoreBroker 启动时打开 `%LOCALAPPDATA%\WinWidgetBoard\data.db` 并完成 schema migration；LauncherHost 继续不加载 SQLite；
- 便签正文不进入日志、剪贴板历史或错误的 developer message。

## 后果

- WorkspacePanel 后续只依赖契约，不直接接触数据库实体；
- CoreBroker 启动失败时应用无法提供持久化便签能力，需要后续增加可见的存储失败状态；
- IPC 重试不会因为重复创建/删除而扩大副作用；
- 真实 UI 输入、失焦、关闭和恢复交互仍需单独验收。
