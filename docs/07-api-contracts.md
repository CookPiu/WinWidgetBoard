# 当前接口与数据契约

文档状态：已批准
协议版本：1.0
日期：2026-08-15

本文只描述当前核心五项实际使用的本地协议。插件、云同步、通用权限和声明式 UI Schema 不属于当前契约。

## 1. 帧与 Envelope

命名管道消息格式：

```text
4 bytes unsigned little-endian payload length
N bytes UTF-8 JSON
```

约束：

- 最大 1 MiB；
- 零长度和超限长度在分配 payload 前拒绝；
- 属性使用 camelCase；
- 时间使用 ISO 8601 UTC；
- 未知主版本、必填字段或枚举拒绝。

Envelope：

```json
{
  "protocolVersion": "1.0",
  "messageType": "request",
  "messageId": "uuid",
  "correlationId": null,
  "sentAtUtc": "2026-08-15T10:00:00.000Z",
  "method": "notes.get",
  "payload": {},
  "error": null
}
```

`messageType` 只允许 `request`、`response`、`event`。响应的 `correlationId` 必须等于请求 `messageId`。

## 2. 会话

连接后首先调用 `session.hello`，携带：

- client type 和版本；
- process ID 和架构；
- 当前启动生成的 session token；
- 支持的协议范围。

成功后返回选择的协议版本、session ID、能力和消息上限。握手前的其他方法全部拒绝。

`session.ping` 用于保活。断线后客户端重新连接、握手，并重新上报面板可见性和卡片订阅。

令牌不得写入日志。

## 3. 当前方法

| 方法 | 用途 | 写操作 |
| --- | --- | --- |
| `session.hello` | 握手 | 否 |
| `session.ping` | 保活 | 否 |
| `panel.report-visibility` | 设置面板可见状态 | 是 |
| `layout.get` | 读取布局 | 否 |
| `layout.save` | 保存布局 | 是 |
| `notes.get` | 读取便签 | 否 |
| `notes.search` | 搜索/列出便签 | 否 |
| `notes.save` | 创建或更新便签 | 是 |
| `notes.delete` | 删除便签 | 是 |
| `cards.subscribe` | 设置连接级卡片订阅 | 是 |
| `cards.snapshot` | 服务端快照事件 | 事件 |
| `weather.settings.get` | 读取天气位置 | 否 |
| `weather.settings.save` | 保存天气位置 | 是 |

写操作使用 `clientOperationId` 防止重试扩大副作用。相同 ID 和相同 payload 返回第一次结果；相同 ID 搭配不同 payload 返回 `validation.invalid-argument`。

## 4. 面板可见性

`panel.report-visibility` payload：

```json
{
  "clientOperationId": "uuid",
  "panelVisible": true,
  "displayId": null
}
```

这是状态设置，不是 toggle。Broker 重启后客户端使用新的 operation ID 重报最新目标状态。

## 5. 布局

布局项保存：

- `instanceId`；
- 连续 `order`；
- `sizeId`：`s`、`m`、`l`、`w`、`xl`；
- 可选 `preferredColumn`；
- 可选 `preferredRow`。

不保存绝对像素。`layout.save` 必须带：

- `layoutId`；
- `displayId`；
- `expectedRevision`；
- `clientOperationId`；
- 完整布局项集合。

revision 不匹配返回 `conflict.layout-revision`。WorkspacePanel 只在完成编辑时保存一次。

## 6. 便签

便签 DTO 包含：

- `noteId`；
- `title`；
- `body`；
- `bodyFormat`；
- `updatedAtUtc`。

限制：

- `noteId` 最长 200 字符；
- 标题最长 512 字符；
- 正文最长 256 KiB；
- 搜索词最长 256 字符；
- `bodyFormat` 只允许 `plain-text` 或 `markdown`。

更新和删除必须携带 `expectedUpdatedAtUtc`。过期 revision 返回 `conflict.notes-revision`，记录不存在返回 `resource.not-found`。

正文不得写入日志或错误详情。

## 7. 卡片快照

`CardStateSnapshot` 包含：

```json
{
  "instanceId": "demo.weather",
  "cardTypeId": "builtin.weather",
  "schemaVersion": 1,
  "sequence": 42,
  "generatedAtUtc": "2026-08-15T10:00:00.000Z",
  "validUntilUtc": "2026-08-15T10:15:00.000Z",
  "status": "ready",
  "freshness": "fresh",
  "payload": {},
  "allowedActions": [],
  "diagnosticCode": null
}
```

状态只允许：

- `loading`；
- `ready`；
- `empty`；
- `offline`；
- `permission-required`；
- `unavailable`；
- `error`；
- `disabled`。

UI 只应用 sequence 更大的快照，不从缺失 payload 推断动作或权限。

## 8. 订阅与背压

`cards.subscribe` 设置本连接关心的实例和可见实例：

```json
{
  "instanceIds": ["demo.weather", "demo.notes"],
  "visibility": {
    "panelVisible": true,
    "visibleInstanceIds": ["demo.weather"]
  }
}
```

规则：

- 每连接至多一个订阅，新请求替换旧请求；
- 最多 100 个实例；
- 可见集合必须是订阅集合的子集；
- 初始响应可以携带最新缓存快照；
- 后续使用 `cards.snapshot` Event Envelope；
- 队列按实例合并，默认最多 32 个待发送实例；
- 溢出时断开慢客户端，不建立无界队列；
- 重连后重新订阅。

## 9. 天气位置

仅支持内置实例 `demo.weather`。

读取：

```json
{
  "method": "weather.settings.get",
  "payload": {
    "instanceId": "demo.weather"
  }
}
```

保存：

```json
{
  "method": "weather.settings.save",
  "payload": {
    "clientOperationId": "uuid",
    "instanceId": "demo.weather",
    "label": "Tokyo",
    "latitude": 35.6762,
    "longitude": 139.6503,
    "expectedRevision": 0
  }
}
```

限制：

- label 最长 80 字符，不含控制字符；
- latitude 为有限数值且在 `[-90, 90]`；
- longitude 为有限数值且在 `[-180, 180]`；
- revision 冲突返回 `conflict.weather-settings-revision`。

未保存时返回 Singapore 默认值和 revision `0`，读取不会写数据库。

保存成功后 Broker 替换天气请求 key、继承当前可见性并发布 Loading 快照。天气 payload 不持久化。

## 10. 天气 Provider

当前唯一网络 Provider：

```text
providerId: app.winwidgetboard.weather.open-meteo
capability: weather.current
domain: api.open-meteo.com
visible cadence: 15 minutes
deadline: 10 seconds
```

请求只携带位置标签、纬度、经度和单位，不携带账号、设备标识或自动定位信息。

失败时保留当前进程内同位置的最近成功 payload，并通过 Offline、RateLimited、Error 或 Unavailable 状态表达。

## 11. 稳定错误码

当前需要保持兼容的错误码包括：

```text
protocol.version-mismatch
protocol.invalid-frame
protocol.message-too-large
session.unauthorized
validation.invalid-argument
resource.not-found
resource.unavailable
conflict.notes-revision
conflict.layout-revision
conflict.weather-settings-revision
provider.timeout
provider.failed
storage.migration-failed
internal.error
```

错误可以包含 correlation ID、是否瞬时和 retry-after，但不得包含令牌、便签正文或完整用户路径。

## 12. 版本规则

- 协议主版本不同：拒绝；
- 较新的可选字段：忽略或保留；
- 数据库 schema 与 IPC 版本独立；
- 当前只有协议 `1.0`；
- 不为延期功能预留新字段或空接口。

## 13. 最小契约测试

变更相关测试至少覆盖：

- 分片 frame、零长度和超限；
- 畸形 JSON、未知版本和缺少字段；
- 写操作幂等冲突；
- layout/note/weather revision 冲突；
- 快照乱序；
- 慢客户端背压；
- 错误不泄露秘密。
