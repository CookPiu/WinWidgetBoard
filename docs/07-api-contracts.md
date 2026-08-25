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
| `weather.summary.get` | 读取最后一次成功天气读数的短投影 | 否 |
| `weather.locations.search` | 按地名搜索可选位置 | 否 |
| `sysmon.settings.get` | 读取硬件监控显示项 | 否 |
| `sysmon.settings.save` | 保存硬件监控显示项 | 是 |
| `sysmon.summary.get` | 读取任务栏入口用的已排版读数分段 | 否 |

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

### 序列号空间

`sequence` 是 **CoreBroker 自己的计数器**。面板内部
也会为卡片生成本地快照（占位、可见
性调度），那是另一个计数器。两者
**不得相互比较**：否则本地序号一旦超
前，Broker 推送的真实数据会被当作「更旧
」丢弃。实现上：首条 Broker 快照无条件
生效，之后仅在 Broker 自身序列空间内保证
递增。

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

摘要读取：

```json
{
  "method": "weather.summary.get",
  "payload": {
    "instanceId": "demo.weather"
  }
}
```

`conditionIconId` 是把 WMO 天气码归约后的稳定标记，取值限于
`clear-day`、`clear-night`、`partly-cloudy-day`、`partly-cloudy-night`、`cloudy`、`fog`、
`drizzle`、`rain`、`snow`、`thunderstorm`、`unknown`，长度不超过 32。调用方只做标记到图形的映射，
不自行解释 WMO 码；未收录的码一律归为 `unknown`，由调用方画中性标记，而不是猜一个相近的天气。

响应 `summary` 在本进程尚无成功读数时为 `null`，调用方据此呈现不可用态，不得填充占位值：

```json
{
  "summary": {
    "instanceId": "demo.weather",
    "label": "Singapore",
    "temperatureText": "32",
    "conditionIconId": "partly-cloudy-night",
    "observedAtUtc": "2026-08-20T06:55:00.0000000+00:00",
    "isStale": false
  }
}
```

限制：

- `instanceId` 必须是内置天气实例，否则返回 `validation.invalid-argument`；
- `temperatureText` 是四舍五入到整数的摄氏度文本，最长 16 字符，供最小原生客户端直接显示；
- 只读，不写数据库，也不改变刷新节奏；天气 payload 仍不持久化。

该方法供任务栏入口在面板关闭时显示天气，配合 [ADR-0024](adr/0024-background-provider-keepalive.md) 的每小时后台刷新。

### weather.locations.search

将用户输入的地名解析为可选位置。该方法是**唯一会把用户输入的文本发往远端的方法**（[ADR-0027](adr/0027-weather-location-search.md)），也是唯一的异步命令：它不占用路由器全局锁，因为它不读写任何共享状态。

```json
{
  "method": "weather.locations.search",
  "payload": {
    "query": "Hangzhou"
  }
}
```

限制：`query` 去空白后为 2～64 字符且不含控制字符，否则返回 `validation.invalid-argument`；最多返回 8 条；响应上限 64 KiB；超时 8 秒。缺少坐标或坐标越界的条目被丢弃，不半填展示。

无匹配返回**空数组**，不是错误；只有请求本身失败才返回 `resource.unavailable`。调用方据此区分「没有这个地方」与「搜索不可用」，两者在界面上是不同的提示。

```json
{
  "results": [
    {
      "name": "Hangzhou",
      "region": "Zhejiang",
      "country": "China",
      "countryCode": "CN",
      "latitude": 30.29365,
      "longitude": 120.16142,
      "timezone": "Asia/Shanghai"
    }
  ]
}
```

`region` 与 `country` 只用于区分同名地点。搜索结果不缓存、不落盘；落盘的仍然只有标签与经纬度。


## 9.1 硬件监控

三个方法，一个内置实例 `demo.sysmon`。

天气卡片载荷在 `current` 之外还带两段预报，均可为空数组——预报是补充信息，只有实况的卡片仍是
可用的卡片，面板据「有没有」决定画不画，而不是另设一个「有预报」标志位：

```json
{
  "hourly": [ { "timeLocal": "2026-08-25T09:00", "temperatureC": 30.9, "weatherCode": 3, "isDay": true, "conditionIconId": "cloudy" } ],
  "daily":  [ { "dateLocal": "2026-08-26", "highTemperatureC": 24.6, "lowTemperatureC": 19.2, "weatherCode": 61, "conditionIconId": "rain" } ]
}
```

- `hourly` 最多 **12** 条，自**当前观测小时**起算。Open-Meteo 的 `hourly.time` 从当地零点开始，
  因此窗口按与 `current.time` 比较定位，而不是取数组开头——取开头会把今天早上当成预报；
- `daily` 最多 **3** 条，跳过下标 0（今天，实况已覆盖），即「明天起的三天」；
- `daily` 的 `conditionIconId` 一律按白天取：一整天的概括用夜间字形会读成「今晚」而不是「周三」；
- 单条畸形即截断该列表，不影响实况——实况才是这张卡片的职责。

### sysmon.settings.get / sysmon.settings.save

显示项配置的是**显示什么**，不是采集什么：一次采样读取整台机器，所以改显示项不改采样，
provider 的请求键从不变化。`networkInterfaceId` 是唯一的例外——它改变**采什么**，因此保存后
直接下推到采样器并丢弃基线。

```json
{
  "instanceId": "demo.sysmon",
  "cardItems":  [ { "metricId": "cpu.usage", "detail": "detailed" } ],
  "entryItems": [ { "metricId": "cpu.usage", "detail": "normal" } ],
  "networkInterfaceId": "",
  "revision": 3,
  "updatedAtUtc": "2026-08-21T06:27:21.0998747Z"
}
```

`sysmon.settings.get` 的响应在 `settings` 之外还带一份 `networkInterfaces`：

```json
{
  "settings": { "...": "如上" },
  "networkInterfaces": [ { "id": "{adapter-guid}", "name": "以太网" } ]
}
```

- `cardItems` 与 `entryItems` **各自独立**，各最多 8 项，**顺序即显示顺序**（没有单独的序号字段）；
- 同一列表内不允许重复 `metricId`；未收录的 `metricId` 一律 `validation.invalid-argument`；
- `detail` 取 `compact` / `normal` / `detailed`；
- `networkInterfaceId` 决定网速从哪块网卡测量。**空串表示全部可用网卡合计**，这也是可配置之前
  的含义与默认值；长度上限 `128`，不接受控制字符，非法值一律归一为空串而不是拒读——由更新版本
  写入或损坏的行必须仍能读出来，代价只是回落到旧行为；
- 存的 ID 在当前机器上找不到对应网卡时**不是错误**（拔了线、断了 VPN），此时网速报告为不可用，
  而不是悄悄改回全部合计——后者会让用户以为自己的选择生效了；
- `networkInterfaces` **不落盘**：网卡随插拔和 VPN 变化，保存时记下的列表下次打开就已经过时。
  它只在 `get` 响应里出现，且与采样器使用同一套过滤条件，因此列表不会提供采样器会忽略的网卡；
- 保存受 `expectedRevision` 保护，冲突返回 `conflict.sysmon-settings-revision`。

指标标记（三个进程共用一张表）：`cpu.usage`、`cpu.clock`、`cpu.temperature`、`memory.usage`、
`gpu.usage`、`gpu.memory`、`gpu.temperature`、`disk.activity`、`disk.usage`、`net.up`、
`net.down`、`fan.speed`。

### sysmon.summary.get

任务栏入口用的只读投影。返回的 `text` 是**成品字符串**：Broker 负责单位与取整，
入口不做格式化也不做本地化，与 `weather.summary.get` 同一纪律。

```json
{
  "summary": {
    "instanceId": "demo.sysmon",
    "segments": [
      {
        "metricId": "cpu.usage",
        "iconId": "cpu",
        "text": "CPU 6%",
        "history": [0.04, 0.11, 0.06]
      },
      {
        "metricId": "net.down",
        "iconId": "net-down",
        "text": "7.2 MB/s",
        "history": [0.0, 0.62, 1.0]
      }
    ],
    "sampledAtUtc": "2026-08-21T06:27:20.4398792+00:00"
  }
}
```

`history` 是入口画迷你走势图用的最近采样，**由旧到新、已归一化到 0..1**，最多 60 个点
（2 秒节奏 ≈ 2 分钟）。归一化留在 Broker，理由与排版相同：百分比类指标有 0..100 的固定刻度、
用量类有自己的上限，而速率类没有任何上限、只能按窗口内峰值相对绘制——这个选择需要入口从来看不到的
原始数值。本机读不到的指标（温度、风扇、CPU 频率）返回空数组；空闲网卡返回真实的一串 0，
两者由入口区别对待。

首次采样之前 `summary` 为 `null`——调用方显示自己的不可用状态，不展示占位读数。

**索要摘要本身就是需求信号**：Provider 仅在卡片可见、或近 10 秒内有过一次
`sysmon.summary.get` 时才采样，两者皆无时休眠（[ADR-0028](adr/0028-system-monitor-scope-and-sensor-tiers.md)）。

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
conflict.sysmon-settings-revision
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
