# ADR-0020：首个天气 Provider 与网络、缓存和隐私边界

状态：Accepted
日期：2026-08-15

## 背景

M2.3.5～M2.3.8 已完成 Scheduled Provider 调度、生产 pump、共享卡片快照、
`cards.subscribe` 和 WorkspacePanel 实际订阅，但仍没有真实数据源。天气卡片需要
一个无需账号、可取消、字段可控且能在失败时保留最近有效结果的首个 Provider。

## 决策

1. 首个天气源采用 Open-Meteo Weather Forecast API 的
   `https://api.open-meteo.com/v1/forecast`，Provider ID 为
   `app.winwidgetboard.weather.open-meteo`，能力为 `weather.current`，只请求
   `temperature_2m`、`relative_humidity_2m`、`apparent_temperature`、
   `weather_code` 和 `wind_speed_10m`。接口字段以
   [Open-Meteo Weather API 文档](https://open-meteo.com/en/docs) 为准。
2. 首个默认位置是手动配置的 Singapore 坐标 `1.3521, 103.8198`。本包不启用
   Windows 自动定位，不发送 Windows 账号、设备标识或其他用户资料；后续设置 UI
   通过同一 `latitude`/`longitude` 参数替换默认值。
3. 网络请求只使用 HTTPS，独立 `HttpClient`、系统证书验证和 64 KiB 响应上限。
   请求由 Scheduler 的 10 秒 deadline 取消；Provider 自身不创建 Timer 或永久循环。
4. 可见天气实例默认每 15 分钟刷新；卡片不在面板可见视口、面板关闭或连接断开时，
   `ProviderRefreshVisibilityRegistry` 将对应注册置为不可运行。节能状态的 OS 事件
   尚未接入，本包不在节能状态下主动发起天气请求。
5. 缓存采用当前 CoreBroker 进程内、按位置参数隔离的最近成功 payload。网络失败、
   429、无效响应或服务端错误保留该 payload，并显式映射为 Offline、RateLimited、
   Error 或 Unavailable；没有最近成功数据时返回空 payload。天气缓存暂不跨进程或跨
   重启持久化，避免在隐私设置和数据删除入口完成前扩大保留范围。
6. 卡片显示 `Weather data by Open-Meteo.com` 并链接到 Open-Meteo。数据遵循
   [Open-Meteo CC BY 4.0 许可说明](https://open-meteo.com/en/license)。当前免费 API
   仅作为本项目非商业开发/评估源；若产品进入商业分发或需要更高配额，必须重新评审
   商业许可和专用 endpoint，不得把当前免费 endpoint 直接当作商业上线方案。

## 后果

- 真实 HTTP 数据现在可以沿 CoreBroker Scheduler → Provider Adapter → Named Pipe →
  WorkspacePanel Weather 卡片完整流动；
- WEA-001 的默认坐标、当前天气、15 分钟刷新、离线最后成功数据和来源标识已具备；
- 自动定位、城市搜索、设置持久化、跨重启天气缓存、OS 网络/电源事件和手动刷新动作
  仍是后续工作包，不得把当前默认位置描述为完整设置能力；
- Open-Meteo 的免费服务存在非商业和配额条件，生产发布前必须重新确认许可证和容量。

## 被否决方案

- **默认自动定位**：会引入额外权限、位置数据处理和不可见网络行为；
- **Provider 自建 Timer**：破坏 M2.3.5 的集中调度、可见性降频和资源预算；
- **立即写入 SQLite 天气缓存**：在删除入口和隐私面板未完成前扩大数据保留范围；
- **同时接入多个天气源**：增加故障、许可、域名白名单和一致性范围，不适合作为首个
  可验证垂直切片。
