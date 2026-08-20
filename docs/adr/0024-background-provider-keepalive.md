# ADR-0024：允许有界的后台刷新，入口在面板关闭时也能显示天气

状态：Accepted
日期：2026-08-20
关系：修改 [ADR-0019](0019-cards-subscription-event-transport.md) 与 [ADR-0021](0021-weather-location-settings.md) 中「面板不可见即停止刷新」的推论；扩展 [ADR-0023](0023-embedded-taskbar-entry-strip.md)

## 背景

产品原则「按需运行」此前被实现为：没有面板连接就完全不刷新。具体机制是 `ProviderRefreshScheduler.GetCadenceLocked` 在所有消费者 `DisplayConnected == false` 时返回 `null`，而 `ProviderRefreshVisibilityRegistry` 只在有面板 `cards.subscribe` 连接时把该标志置真。因此 `ScheduledProviderDescriptor.HiddenInterval` 这条「隐藏时低频刷新」的通路从未被走到。

ADR-0023 把入口做成了任务栏内的信息条，用户要求它能显示天气。天气只在面板打开时才刷新，入口就永远没有可显示的读数。

用户明确调整了取舍：**允许一部分服务在后台运行，前提是尽量减少资源占用。**

## 决策

1. 产品原则 5 由「面板关闭后停止非必要工作」改为「面板关闭后只保留有界的低频后台工作，并可测量」。
2. `ProviderRefreshVisibilityRegistry.Register` 增加 `keepWarmWithoutPanel`。置真的实例在没有任何面板连接时仍视为 `DisplayConnected`，但**不视为** `PanelVisible`，因此落到 `HiddenInterval` 这条低频通路。
3. 天气实例注册为 keep-warm，`HiddenInterval` 定为 1 小时（可见时仍是 15 分钟）。
4. 新增只读方法 `weather.summary.get`，返回最后一次成功读数的短投影（标签、整数摄氏度文本、观测时间、是否过期）。首次成功刷新前返回空，调用方自行呈现不可用态。
5. 天气 payload 仍然只留在 Broker 进程内，**不写入 SQLite**；摘要同样只是进程内投影。
6. LauncherHost 通过既有的最小原生客户端轮询该方法：拿到读数前每 3 秒一次，之后每分钟一次。

## 理由

- 复用既有 `HiddenInterval` 通路，不新增调度概念；
- keep-warm 是按实例的显式选择，不是全局放开，其他 provider 行为不变；
- 摘要是只读投影且在运行时侧无锁，不会排在设置保存后面；
- 返回预格式化的温度文本，使最小原生客户端不必解析 JSON 数字。

## 后果

正面：

- 入口在面板关闭时可显示天气；
- 面板打开时天气已经是热的，不必等首次请求。

负面与约束：

- 常驻进程有了持续的后台工作。参考机实测（Release x64，90 秒空闲采样，无面板）：CoreBroker 消耗 422 ms CPU（全核 0.033%）、工作集 52～57 MB；LauncherHost 消耗 78 ms CPU、工作集 11.6 MB。作为对比，后台天气启用前是 CoreBroker 109 ms / 60.9 秒（0.013%）、43～46 MB，LauncherHost 0 ms。CoreBroker 的 CPU 数字包含采样窗口内的那一次启动刷新，不代表每小时稳态。
- 关闭面板不再意味着零网络活动；隐私边界未变（仍只有 Open-Meteo，仍不发送账号或设备标识），但用户应当知道后台会按小时请求。
- `weather.summary.get` 进入 IPC 兼容面，错误码沿用既有 `validation.invalid-argument` / `resource.unavailable`。

## 放弃方案

- **让原生客户端订阅 `cards.subscribe`**：ADR-0008 要求 LauncherHost 只保留最小原生客户端，长连接事件流与背压不属于其职责。
- **把天气读数写入 SQLite 供入口读取**：违反 ADR-0020/0021 的「天气 payload 不落盘」，且 LauncherHost 不得访问数据库。
- **全局取消可见性暂停**：会让所有 provider 无条件后台运行，超出本次取舍范围。

## 验证

- `IT-WEA-003` 覆盖首次刷新前返回空与实例校验；
- `IT-WEA-004` 用桩 HTTP 覆盖完整路径：无面板可见时 pump 启动请求、摘要被捕获、经 IPC 返回；
- `UT-WEA-005` 约束隐藏节奏为 1 小时且长于可见节奏；
- 真实桌面：Broker 与入口同时运行、无面板时入口显示 `32° Singapore`；
- 空闲占用由 `scripts/Measure-StartupFootprint.ps1` 实测，数字记录在实施状态页。
