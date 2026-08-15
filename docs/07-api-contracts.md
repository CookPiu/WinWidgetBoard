# 接口与数据契约

文档状态：提案基线
版本：0.1
协议版本：1.0
日期：2026-08-06

本文定义逻辑契约。实际 JSON Schema 文件应在 M2.0 生成到 `src/Contracts/Schemas/`，并由契约测试验证。

M2.0.1 已实现 `Envelope`、UTF-8 JSON 编解码、1MiB 消息上限和 4 字节 little-endian 长度帧；M2.0.2 已实现当前用户 Named Pipe、`session.hello`/`session.ping`、CoreBroker 单实例，以及客户端请求超时、定时心跳和基础断线重连 POC；LauncherHost/WorkspacePanel 生产客户端接入、真实业务方法和完整重启恢复仍未实现。

## 1. 基本约定

### 1.1 编码

- UTF-8；
- 属性名 camelCase；
- 时间使用 ISO 8601 UTC，例如 `2026-08-06T09:30:00.000Z`；
- ID 使用小写 UUID 或受约束的反向域名；
- 枚举使用小写 kebab-case；
- 空值与缺失语义不同；
- 未知可选字段可保留，未知必填字段或主版本拒绝。

### 1.2 传输帧

命名管道上的每条消息：

```text
4 bytes: unsigned little-endian payload length
N bytes: UTF-8 JSON payload
```

约束：

- 默认最大 1MiB；
- 长度为 0 拒绝；
- 超限在分配 payload 缓冲区前拒绝；
- 每次读取必须处理部分 frame；
- 二进制数据不得 base64 塞入常规状态消息。

### 1.3 Envelope

```json
{
  "protocolVersion": "1.0",
  "messageType": "request",
  "messageId": "5f6060f1-7a4f-482d-81bd-9e9c5f6d130d",
  "correlationId": null,
  "sentAtUtc": "2026-08-06T09:30:00.000Z",
  "method": "panel.toggle",
  "payload": {},
  "error": null
}
```

`messageType`：

- `request`；
- `response`；
- `event`。

响应的 `correlationId` 等于请求 `messageId`。事件可没有 correlationId。

## 2. 握手

### 2.1 请求

```json
{
  "protocolVersion": "1.0",
  "messageType": "request",
  "messageId": "uuid",
  "sentAtUtc": "2026-08-06T09:30:00.000Z",
  "method": "session.hello",
  "payload": {
    "clientType": "workspace-panel",
    "clientVersion": "0.1.0",
    "processId": 1234,
    "architecture": "x64",
    "sessionToken": "<redacted>",
    "supportedProtocolRange": {
      "min": "1.0",
      "max": "1.0"
    }
  }
}
```

### 2.2 响应

```json
{
  "protocolVersion": "1.0",
  "messageType": "response",
  "messageId": "uuid",
  "correlationId": "request-uuid",
  "sentAtUtc": "2026-08-06T09:30:00.010Z",
  "method": "session.hello",
  "payload": {
    "acceptedProtocolVersion": "1.0",
    "serverVersion": "0.1.0",
    "sessionId": "uuid",
    "capabilities": [
      "cards.subscribe",
      "layout.write",
      "timers"
    ],
    "maxMessageBytes": 1048576
  },
  "error": null
}
```

失败：

- token 错误；
- 当前用户不匹配；
- 主版本不兼容；
- clientType 未知；
- 消息超限。

握手成功前的其他方法一律拒绝。

## 3. 标识符

### 3.1 Card Type ID

反向域名格式：

```text
app.winwidgetboard.weather
app.winwidgetboard.notes
com.example.package-card
```

规则：

- 3～128 字符；
- 小写 ASCII、数字、点、连字符；
- 不允许连续点；
- 内置 `app.winwidgetboard.*` 保留。

### 3.2 Instance ID

UUID。两个相同 Card Type 的放置副本必须有不同 Instance ID。

### 3.3 Provider ID

反向域名格式。Card Type 可以依赖一个或多个 Provider。

## 4. Card Definition

```json
{
  "cardTypeId": "app.winwidgetboard.weather",
  "displayNameKey": "card.weather.name",
  "descriptionKey": "card.weather.description",
  "category": "information",
  "version": "1.0.0",
  "source": "builtin",
  "supportedSizes": [
    { "id": "s", "columns": 1, "rows": 1 },
    { "id": "m", "columns": 2, "rows": 1 },
    { "id": "l", "columns": 2, "rows": 2 }
  ],
  "defaultSizeId": "m",
  "requiredProviders": ["app.winwidgetboard.weather-provider"],
  "requiredPermissions": ["network"],
  "settingsSchemaVersion": 1,
  "uiSchemaVersion": 1
}
```

`source`：

- `builtin`；
- `brokered-plugin`；
- `full-trust-plugin`。

## 5. Card Instance

```json
{
  "instanceId": "c1fe6ef7-6073-4fba-8e82-208df7c5d33e",
  "cardTypeId": "app.winwidgetboard.weather",
  "enabled": true,
  "sizeId": "m",
  "settingsRevision": 4,
  "settings": {
    "locationMode": "manual",
    "city": "Singapore",
    "units": "metric"
  },
  "createdAtUtc": "2026-08-06T08:00:00.000Z",
  "updatedAtUtc": "2026-08-06T09:00:00.000Z"
}
```

设置更新必须带 expected revision：

```json
{
  "instanceId": "uuid",
  "expectedRevision": 4,
  "settings": {}
}
```

不匹配返回 `conflict.settings-revision`，避免覆盖其他窗口修改。

## 6. Layout

### 6.1 Layout Snapshot

```json
{
  "layoutId": "primary-default",
  "displayId": "\\\\?\\DISPLAY#...",
  "revision": 12,
  "columnPolicy": {
    "small": 2,
    "normal": 4,
    "wide": 6
  },
  "items": [
    {
      "instanceId": "uuid-1",
      "order": 0,
      "columnSpan": 2,
      "rowSpan": 1,
      "preferredColumn": 0
    },
    {
      "instanceId": "uuid-2",
      "order": 1,
      "columnSpan": 2,
      "rowSpan": 2,
      "preferredColumn": null
    }
  ],
  "updatedAtUtc": "2026-08-06T09:00:00.000Z"
}
```

最终行列位置不持久化，由布局引擎计算。`preferredColumn` 只是提示。

### 6.2 Layout Command

```json
{
  "commandType": "move-card",
  "layoutId": "primary-default",
  "expectedRevision": 12,
  "instanceId": "uuid-1",
  "targetOrder": 3,
  "preferredColumn": 2,
  "clientOperationId": "uuid"
}
```

命令类型：

- `add-card`；
- `remove-card`；
- `move-card`；
- `resize-card`；
- `restore-default`；
- `undo`；
- `redo`。

CoreBroker 提交后返回新 revision 和规范化布局。

### 6.3 Layout IPC Methods

- `layout.get`：按 `layoutId` 和 `displayId` 读取布局快照；不存在时返回 `resource.not-found`；
- `layout.save`：带 `clientOperationId`、`expectedRevision` 和完整布局项集合保存布局；
- `layout.save` 的 `sizeId` 只允许 `s`、`m`、`l`、`w`、`xl`，`order` 必须从零连续递增；
- 布局项的 `preferredColumn`、`preferredRow` 是可选逻辑网格坐标，不是绝对像素坐标；保存时使用当前 placement 的实际行列；
- 重复的 `clientOperationId` 与相同请求返回第一次结果，revision 不重复递增；
- revision 不匹配返回 `conflict.layout-revision`；
- WorkspacePanel 只在完成编辑时提交一次，不在拖动每帧写入 SQLite。

## 7. Card State Snapshot

```json
{
  "instanceId": "uuid",
  "cardTypeId": "app.winwidgetboard.system-monitor",
  "schemaVersion": 1,
  "sequence": 424,
  "generatedAtUtc": "2026-08-06T09:30:00.000Z",
  "validUntilUtc": "2026-08-06T09:30:05.000Z",
  "status": "ready",
  "freshness": "fresh",
  "payload": {
    "cpuPercent": 18.2,
    "memoryPercent": 53.4,
    "networkDownBytesPerSecond": 3090,
    "networkUpBytesPerSecond": 1460
  },
  "allowedActions": [
    "refresh",
    "open-details"
  ],
  "diagnosticCode": null
}
```

`status`：

- `loading`；
- `ready`；
- `empty`；
- `offline`；
- `permission-required`；
- `unavailable`；
- `error`；
- `disabled`。

`freshness`：

- `fresh`；
- `stale`；
- `expired`（保留值，当前 M2.3.6 DTO 以 `stale` 表达过期）；
- `unknown`。

WorkspacePanel 只接受 sequence 大于当前已应用 sequence 的快照。

Provider 结果在 CoreBroker 内先转换为 `CardStateSnapshot`，再进入订阅传输。转换必须
为每个 `instanceId` 独立递增 `sequence`，保留 JSON payload 的所有权，并显式给出
`status`、`freshness`、`diagnosticCode` 和 `allowedActions`；WorkspacePanel 不根据
payload 缺失字段推断动作或权限。当前快照合同不增加独立的 `expired` 枚举，过期时间
通过 `freshness: "stale"` 表达。

## 8. Action

### 8.1 请求

```json
{
  "protocolVersion": "1.0",
  "messageType": "request",
  "messageId": "uuid",
  "sentAtUtc": "2026-08-06T09:30:00.000Z",
  "method": "card.execute-action",
  "payload": {
    "instanceId": "uuid",
    "actionId": "timer.start",
    "expectedStateSequence": 18,
    "arguments": {
      "durationSeconds": 1500
    },
    "clientOperationId": "uuid"
  }
}
```

### 8.2 幂等

有外部副作用的命令必须支持 `clientOperationId` 去重。重复请求返回第一次结果，不重复创建计时器或待办。

## 9. Panel API

### 9.1 Launcher 到 Panel 启动上下文

```json
{
  "command": "show",
  "displayId": "\\\\?\\DISPLAY#...",
  "monitorRectPx": {
    "left": 0,
    "top": 0,
    "right": 1920,
    "bottom": 1080
  },
  "workAreaRectPx": {
    "left": 0,
    "top": 0,
    "right": 1920,
    "bottom": 1032
  },
  "launcherRectPx": {
    "left": 8,
    "top": 1036,
    "right": 44,
    "bottom": 1072
  },
  "dpi": 120,
  "sessionToken": "<redacted>"
}
```

启动参数中的 token 不写日志。更理想的实现可通过继承句柄或受保护临时通道传递。

### 9.2 Panel Methods

- `panel.show`；
- `panel.hide`；
- `panel.toggle`；
- `panel.open-card`；
- `panel.open-settings`；
- `panel.enter-modal-scope`；
- `panel.exit-modal-scope`；
- `panel.report-visibility`。

### 9.3 Panel Visibility Report

WorkspacePanel 使用状态设置式上报同步当前面板可见性：

```json
{
  "method": "panel.report-visibility",
  "payload": {
    "clientOperationId": "uuid",
    "panelVisible": true,
    "displayId": null
  }
}
```

成功响应的 `payload`：

```json
{
  "clientOperationId": "uuid",
  "panelVisible": true,
  "revision": 1,
  "acceptedAtUtc": "2026-08-07T09:30:00.000Z"
}
```

相同 `clientOperationId` 和相同 payload 的重复请求返回相同业务结果，不重复应用状态；同一 ID 搭配不同 payload 返回 `validation.invalid-argument`。Broker 重启后客户端重新握手，并以最新目标状态生成新的 operation ID 重新上报。该状态目前仅存在于 Broker 进程内，不替代 M2.1 持久化。

### 9.4 Notes Methods

便签通过当前用户 CoreBroker IPC 访问，WorkspacePanel 不直接打开 SQLite。支持的方法：

- `notes.save`：创建或带 `expectedUpdatedAtUtc` 的更新；写请求必须带 `clientOperationId`；
- `notes.get`：按 `noteId` 读取；
- `notes.search`：按标题或正文搜索；
- `notes.delete`：带 `noteId`、`expectedUpdatedAtUtc` 和 `clientOperationId` 删除。

`bodyFormat` 只允许 `plain-text` 或 `markdown`。`noteId` 最长 200 字符，标题最长 512 字符，正文最长 256 KiB，搜索词最长 256 字符。正文和时间戳不写入日志。

保存请求示例：

```json
{
  "method": "notes.save",
  "payload": {
    "clientOperationId": "uuid",
    "noteId": "note-1",
    "title": "今日记录",
    "body": "# 内容",
    "bodyFormat": "markdown",
    "expectedUpdatedAtUtc": null
  }
}
```

更新或删除使用过期 `expectedUpdatedAtUtc` 时返回 `conflict.notes-revision`；不存在的记录返回 `resource.not-found`；相同 `clientOperationId` 搭配不同 payload 返回 `validation.invalid-argument`。

### 9.5 Weather Settings Methods

天气位置设置只针对内置实例 `demo.weather`，通过当前用户 CoreBroker IPC 保存。未保存过时，
`weather.settings.get` 返回默认 Singapore 位置和 revision `0`，不会因为读取而写入数据库。

读取请求：

```json
{
  "method": "weather.settings.get",
  "payload": {
    "instanceId": "demo.weather"
  }
}
```

保存请求与响应：

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

```json
{
  "clientOperationId": "uuid",
  "settings": {
    "instanceId": "demo.weather",
    "label": "Tokyo",
    "latitude": 35.6762,
    "longitude": 139.6503,
    "revision": 1,
    "updatedAtUtc": "2026-08-15T10:20:30.0000000Z"
  }
}
```

位置名称最多 80 个字符且不得包含控制字符；纬度必须在 `[-90, 90]`，经度必须在
`[-180, 180]`，两者都必须是有限数值。保存使用 `expectedRevision` 防止覆盖其他窗口的
修改；冲突返回 `conflict.weather-settings-revision`。相同 `clientOperationId` 和相同字段
返回缓存结果，不重复替换 Provider；同一 ID 搭配不同字段返回
`validation.invalid-argument`。

## 10. Provider Contract

### 10.1 Provider Descriptor

```json
{
  "providerId": "app.winwidgetboard.weather.open-meteo",
  "version": "1.0.0",
  "capabilities": [
    "weather.current"
  ],
  "mode": "scheduled",
  "minimumIntervalSeconds": 900,
  "permissions": [
    "network"
  ],
  "networkDomains": [
    "api.open-meteo.com"
  ]
}
```

### 10.2 Provider Request

```json
{
  "requestId": "uuid",
  "capability": "weather.current",
  "deadlineUtc": "2026-08-06T09:30:10.000Z",
  "arguments": {
    "label": "Singapore",
    "latitude": 1.3521,
    "longitude": 103.8198,
    "units": "metric"
  }
}
```

Provider 不应收到不需要的用户资料。

M2.3.9 的首个实现只请求 Open-Meteo 的 `current` 字段，并将结果归一化为卡片 payload：

```json
{
  "source": "app.winwidgetboard.weather.open-meteo",
  "sourceDomain": "api.open-meteo.com",
  "location": {
    "label": "Singapore",
    "latitude": 1.3521,
    "longitude": 103.8198,
    "timezone": "Asia/Singapore"
  },
  "current": {
    "observedAtLocal": "2026-08-15T18:00",
    "temperatureC": 31.2,
    "apparentTemperatureC": 36.4,
    "relativeHumidityPercent": 72,
    "windSpeedKmh": 11.5,
    "weatherCode": 2
  },
  "attribution": "Weather data by Open-Meteo.com",
  "attributionUrl": "https://open-meteo.com/"
}
```

请求只携带用户选择的位置参数，不携带账号、设备标识或自动定位信息。失败时由
Scheduler/Provider 保留当前进程内同一位置的最近成功 payload，并通过快照状态暴露
Offline、RateLimited、Error 或 Unavailable；该缓存不跨重启持久化。

## 11. Plugin Manifest

```json
{
  "manifestVersion": 1,
  "id": "com.example.focus-tools",
  "name": "Focus Tools",
  "version": "1.2.0",
  "author": {
    "name": "Example",
    "url": "https://example.invalid"
  },
  "minimumHostVersion": "0.5.0",
  "apiVersion": "1.0",
  "securityTier": "brokered",
  "entryPoint": "provider/module.wasm",
  "cards": [
    "cards/pomodoro.json"
  ],
  "permissions": [
    {
      "name": "notifications",
      "reason": "计时结束时提醒"
    }
  ],
  "networkDomains": [],
  "contentHash": "sha256:...",
  "license": {
    "spdx": "MIT",
    "noticeFile": "LICENSES/MIT.txt"
  }
}
```

注意：`brokered` 只在真实沙箱落地后可用。否则必须标记 `full-trust`。

## 12. Permission Grant

```json
{
  "subjectId": "com.example.focus-tools",
  "permission": "notifications",
  "scope": null,
  "decision": "allow",
  "grantedAtUtc": "2026-08-06T09:00:00.000Z",
  "revision": 1
}
```

网络权限 scope：

```json
{
  "domains": [
    "api.example.invalid"
  ],
  "methods": [
    "GET"
  ]
}
```

权限变更后：

- 当前请求可取消；
- Provider 收到 capability change；
- 缓存按策略清理；
- UI 更新为 PermissionRequired 或 Ready。

## 13. 声明式 UI Schema 草案

第三方卡片默认由宿主渲染有限节点：

- `stack`；
- `grid`；
- `text`；
- `icon`；
- `image-token`；
- `button`；
- `toggle`；
- `progress`；
- `sparkline`；
- `list`；
- `input`（受限）；
- `separator`；
- `status-badge`。

示例：

```json
{
  "schemaVersion": 1,
  "root": {
    "type": "stack",
    "orientation": "vertical",
    "spacing": "space-3",
    "children": [
      {
        "type": "text",
        "style": "card-title",
        "textBinding": "title"
      },
      {
        "type": "progress",
        "valueBinding": "progress",
        "minimum": 0,
        "maximum": 1
      },
      {
        "type": "button",
        "labelBinding": "actionLabel",
        "actionId": "timer.toggle"
      }
    ]
  }
}
```

限制：

- 不接受脚本；
- 不接受任意 XAML；
- 不接受任意 URI；
- 图片通过 host token；
- 样式使用宿主 token；
- 节点数、深度和文本长度有上限；
- action 必须出现在 allowed actions。

## 14. 错误

```json
{
  "code": "permission.denied",
  "category": "permission-denied",
  "messageKey": "error.permissionDenied",
  "developerMessage": "clipboard.read was not granted",
  "correlationId": "uuid",
  "isTransient": false,
  "retryAfterSeconds": null,
  "details": {
    "permission": "clipboard.read"
  }
}
```

稳定错误码：

```text
protocol.version-mismatch
protocol.invalid-frame
protocol.message-too-large
session.unauthorized
validation.invalid-argument
permission.denied
resource.unavailable
provider.timeout
provider.failed
storage.conflict
storage.migration-failed
layout.revision-conflict
settings.revision-conflict
conflict.weather-settings-revision
provider.update-failed
security.policy-violation
internal.error
```

## 15. 订阅与背压

WorkspacePanel 订阅：

```json
{
  "method": "cards.subscribe",
  "payload": {
    "instanceIds": ["uuid-1", "uuid-2"],
    "visibility": {
      "panelVisible": true,
      "visibleInstanceIds": ["uuid-1"]
    }
  }
}
```

成功响应使用连接范围内的订阅 ID，并可携带当前已缓存的初始快照：

```json
{
  "subscriptionId": "uuid",
  "initialSnapshots": [
    {
      "instanceId": "uuid-1",
      "cardTypeId": "app.winwidgetboard.system-monitor",
      "schemaVersion": 1,
      "sequence": 424,
      "generatedAtUtc": "2026-08-06T09:30:00.000Z",
      "validUntilUtc": "2026-08-06T09:30:05.000Z",
      "status": "ready",
      "freshness": "fresh",
      "payload": {},
      "allowedActions": ["refresh"],
      "diagnosticCode": null
    }
  ]
}
```

后续状态使用 `Event` Envelope 发送，方法为 `cards.snapshot`，payload 为
`{ "snapshot": <CardStateSnapshot> }`。M2.3.7 已将该事件接入当前用户 Named Pipe：
CoreBroker 握手在具备订阅 hub 时声明 `cards.subscribe` 能力，客户端读取泵按
`MessageType` 和 correlation ID 分流命令响应与异步事件。连接断开后订阅不会跨连接
保留，低层客户端重新握手后必须重新提交订阅请求；WorkspacePanel 的
`CardSnapshotSubscriptionCoordinator` 由 `CoreBrokerSession.Reconnected` 自动完成这次
重订阅。

规则：

- 每个连接至多一个订阅，新请求替换旧订阅；
- `instanceIds` 最多 100 个，`visibleInstanceIds` 必须是 `instanceIds` 的子集；
- `panelVisible=false` 或实例不在可见集合时不发送状态事件；重新可见时重新提交订阅；
- 状态事件可合并，只保留每实例最新 snapshot；
- 命令结果不得丢弃；
- 慢客户端超过默认 32 个待发送实例槽位时断开并记录，客户端必须重新订阅；
- Broker 的初始快照缓存默认最多保留 1024 个实例，不允许无界事件队列或跨连接订阅状态；
- UI 更新按帧批量应用。

## 16. 数据大小限制

建议初值：

| 数据 | 上限 |
| --- | ---: |
| IPC message | 1MiB |
| settings JSON | 64KiB/instance |
| state payload | 256KiB/instance |
| UI schema nodes | 256 |
| UI schema depth | 16 |
| text field | 16KiB，具体卡片可更低 |
| diagnostics detail | 32KiB |
| image token metadata | 8KiB |

超过上限返回验证错误，不截断后继续执行。

## 17. 版本管理

- 协议 major 变化需要双版本迁移窗口；
- schemaVersion 按 Card/Manifest/UI 分别管理；
- 数据库 schema 不等同 IPC 版本；
- 插件最低宿主版本与 API 版本分开；
- 废弃字段至少保留一个稳定发布周期；
- 未知配置字段在读写迁移中尽量保留。

## 18. 契约测试

至少覆盖：

- frame 分片；
- 0 长度、负向溢出、超大长度；
- 非 UTF-8；
- 畸形 JSON；
- 未知 major/minor；
- 缺少必填字段；
- 未知枚举；
- sequence 乱序；
- revision 冲突；
- 重复 clientOperationId；
- 未授权 action；
- manifest 权限与请求不符；
- UI schema 深度/节点/URI 攻击；
- 错误响应不泄露秘密。
