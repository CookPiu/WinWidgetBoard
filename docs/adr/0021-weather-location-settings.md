# ADR-0021：天气位置设置通过版本化本地 IPC 保存

- 状态：Accepted
- 日期：2026-08-15
- 关联需求：WEA-001、SET-001、SET-003、DAT-001、NFR-REL-002、NFR-PRI-003
- 关联 ADR：ADR-0005、ADR-0010、ADR-0012、ADR-0013、ADR-0020

## 背景

M2.3.9 的天气 Provider 使用固定的 Singapore 坐标，仅在 CoreBroker 进程内缓存最近成功的
天气 payload。WEA-001 要求用户可手动选择城市或经纬度，SET-001/SET-003 要求设置入口和可
测试的提交门禁；天气位置属于非敏感本地设置，不应借此引入自动定位权限或跨重启天气数据缓存。

## 决策

1. 新增 `weather.settings.get` 和 `weather.settings.save` 两个当前用户 Named Pipe 方法，
   复用协议版本 1.0、握手、超时和稳定错误码。
2. 设置只允许当前内置天气实例 `demo.weather`，字段为位置名称、纬度、经度、revision 和
   更新时间；位置名称最多 80 个字符，坐标限制为纬度 `[-90, 90]`、经度 `[-180, 180]`，
   且必须是有限数值。
3. CoreBroker 使用 SQLite 第 3 次迁移建立 `weather_settings` 表。保存使用
   `expectedRevision` 和 `clientOperationId`，冲突返回
   `conflict.weather-settings-revision`，重复相同操作返回第一次结果。
4. WorkspacePanel 的天气设置对话框通过 `WeatherSettingsViewModel` 提交，ViewModel 先用
   M2.3.3 的 `CardSettingsDraft` 完成 schema、64 KiB、session/draft/revision 门禁，再调用
   高层 `CoreBrokerWeatherSettingsClient`；取消不会产生 IPC 或外部副作用。
5. 设置保存成功后，CoreBroker 替换天气 Provider 的请求 key 和坐标参数，向已有订阅发布
   Loading 快照，然后由集中 Scheduler 按当前可见性调度新请求。天气 payload 仍只保留在进程内，
   不写入 SQLite。
6. 不在本 ADR 中引入自动定位、城市搜索、跨重启天气缓存、OS 网络/电源事件或多天气实例。

## 后果

- 用户修改天气位置后无需重启 CoreBroker；旧位置快照会先被 Loading 状态遮盖，避免把旧位置的
  数值误显示为新位置结果。
- SQLite 迁移继续在事务和迁移前备份边界内执行；旧数据库会从 schema v1/v2 顺序升级到 v3。
- 位置标签和经纬度属于本地设置，隐私说明仍需展示天气 Provider 的域名、用途和最后访问时间；
  不记录账号、设备标识或自动定位结果。
- 这不是天气数据缓存方案。断网回退仍只覆盖当前进程中同一位置的最近成功 payload，跨重启
  缓存需要后续独立 ADR 和数据删除验收。

## 验证

- `WeatherSettingsRepository` 覆盖迁移后的读写、revision 冲突和输入边界；
- `WeatherSettingsIpcTests` 覆盖 get/save、幂等、冲突、运行时 Provider 注册替换和非法坐标；
- `WeatherSettingsViewModelTests` 覆盖 CardSettingsDraft 提交路径和 IPC 前坐标门禁；
- 真实 UI Automation 脚本覆盖设置入口、三个输入框、保存反馈和天气卡片刷新。
