# ADR-0032：天气经 Windows 前台授权读取设备位置

状态：Accepted
日期：2026-08-29
关系：修改 ADR-0020、ADR-0021 中“不启用自动定位”的范围结论

## 背景

天气已支持地点搜索、手动坐标持久化和运行时 Provider 切换，但用户明确要求天气自动读取
电脑可用的信息定位。直接通过公网 IP 定位会新增第三方服务、不可见网络行为和新的数据共享
边界；Windows 已提供受系统隐私设置控制的 `Windows.Devices.Geolocation`。

## 决策

1. 自动定位只在 `WorkspacePanel` 中实现。`LauncherHost` 和 `CoreBroker` 不加载定位 API，
   不新增常驻定位进程或服务。
2. 正常应用首次前台打开时，在 UI 线程调用 `Geolocator.RequestAccessAsync`。用户允许后，
   每次应用冷启动读取一次位置；验收、smoke 和 broker-only 路径不请求权限。
3. 定位读取接受 5 分钟内缓存，最长等待 10 秒。拒绝授权、系统定位关闭、无数据或 WinRT
   错误均保留现有天气位置，不清空卡片、不写入失败状态。
4. SQLite schema v7 为 `weather_settings` 增加 `use_device_location`。现有行默认迁移为开启，
   以落实本次用户选择；手动选择地点自动关闭该模式，设置页可重新开启并立即更新。
5. Windows 不再提供可用的 `CivicAddress` 城市名，因此自动位置使用本地化标签“当前位置”，
   只保存 Windows 返回的经纬度。不开启额外反向地理编码服务。
6. Windows 权限结果、位置精度、定位来源和定位历史均不落盘、不进日志。既有最终坐标仍按
   revision 保护保存，并只随 Open-Meteo 天气请求发送。
7. 设置页提供 `ms-settings:privacy-location` 入口；关闭自动模式后不调用 Windows 定位，
   继续复用已有地点搜索和手动位置链路。

## 理由

- 位置授权、撤销和系统级提示由 Windows 统一管理；
- 不引入 IP 定位供应商或新的网络白名单；
- 单次冷启动读取满足天气定位，不需要连续跟踪和后台耗电；
- 自动模式与手动模式共享现有 IPC、SQLite、Provider 切换和失败恢复边界。

## 后果与限制

- 首次正常打开可能出现一次 Windows 位置授权提示；拒绝后仍可手动选择地点；
- 台式机无 GPS 时精度取决于 Windows 的网络定位或用户配置默认位置；
- 电脑在应用常驻期间移动不会持续更新，下一次冷启动或设置中的“立即更新”才重新读取；
- 自动坐标会随正常天气请求发往 Open-Meteo，隐私说明和日志禁入规则继续适用。

## 验证

- ViewModel 覆盖允许、拒绝、无数据、手动覆盖和 revision 冲突；
- SQLite 覆盖 v6→v7 迁移及自动模式持久化；
- IPC 覆盖 `useDeviceLocation` 的幂等指纹和响应回读；
- WorkspacePanel Release 构建和 smoke 验证 WinRT/XAML；
- 真实桌面验证 Windows 授权、自动坐标保存、天气刷新、拒绝降级和隐私设置入口。
