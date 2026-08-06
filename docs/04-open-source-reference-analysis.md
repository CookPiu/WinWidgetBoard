# 开源项目参考分析

文档状态：已批准基线
调研日期：2026-08-06
用途：提炼实现模式，不代表允许复制源码

## 1. 结论

未发现一个成熟开源仓库完整覆盖以下组合：

```text
Windows 11 左下角入口
+ 无 Explorer 注入
+ 半屏原生卡片面板
+ 可拖拽响应式网格
+ 便签/日历/计时器/待办/剪贴板/系统监控
+ 低占用
+ 安全隔离插件
```

最合理的做法是新建项目，组合借鉴不同仓库的实现思想：

- TaskbarWidgets：任务栏生命周期、Provider 隔离、状态快照；
- WidBar Widget Template：卡片/浮层/设置生命周期；
- EarTrumpet：入口、Flyout 预热和系统集成；
- FluentFlyout：Windows 11 视觉和弹出面板行为；
- Zebar/Seelen UI：插件包、Provider、主题和市场模型；
- Rainmeter/TrafficMonitor：数据测量和监控生态；
- LibreHardwareMonitor：可选高级传感器。

## 2. 许可证原则

本文件不是法律意见。项目许可证尚未决定，因此采用最保守策略：

1. 当前只记录公开行为、架构和接口思想。
2. 不复制任何第三方代码、资源、图标或测试数据。
3. 引入代码前逐文件确认许可证与 NOTICE 要求。
4. GPL/AGPL 代码仅作行为参考，除非项目明确选择兼容许可证。
5. 带额外限制或非 OSI 许可证的代码不得直接进入仓库。
6. 即使是 MIT，也必须保留版权和许可证文本。
7. 依赖许可证与“复制项目源码”的义务分别审查。

## 3. 参考项目矩阵

| 项目 | 与本项目的关联 | 许可证 | 活跃/风险结论 |
| --- | --- | --- | --- |
| [pfcdev/TaskbarWidgets](https://github.com/pfcdev/TaskbarWidgets) | 任务栏组件运行时、Provider、社区插件 | MIT | 活跃 Beta；使用私有 XAML，稳定性风险高 |
| [andelby/widbar-widget-template](https://github.com/andelby/widbar-widget-template) | WinUI 3 插件契约、Preview/Flyout/Settings | MIT | 模板公开；未发现 WidBar 宿主源码 |
| [File-New-Project/EarTrumpet](https://github.com/File-New-Project/EarTrumpet) | 通知区入口、Flyout、窗口生命周期 | MIT 变体，含实体排除 | 工程成熟；许可证须单独审查 |
| [unchihugo/FluentFlyout](https://github.com/unchihugo/FluentFlyout) | Fluent 2、Mica、任务栏组件、弹窗 | GPL-3.0 | 适合行为参考，不直接复制 |
| [eythaann/Seelen-UI](https://github.com/eythaann/Seelen-UI) | 完整桌面环境、Widget SDK、主题 | AGPL-3.0 | 过重且依赖 WebView，不作为基础 |
| [glzr-io/zebar](https://github.com/glzr-io/zebar) | Widget Pack、Provider、弹窗、市场 | GPL-3.0 | 插件模型优秀，但基于 WebView |
| [rainmeter/rainmeter](https://github.com/rainmeter/rainmeter) | Measure/Skin/Plugin 生态 | GPL-2.0 | 数据与配置思想可参考，UI 模型不适配 |
| [zhongyang219/TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor) | 任务栏监控、插件、硬件信息 | Anti-996 License 1.0 Draft | 非常适合研究需求，不直接复制 |
| [ModernFlyouts](https://github.com/ModernFlyouts-Community/ModernFlyouts) | Windows Flyout 替换 | 已归档 | 仅历史参考 |
| [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 温度、风扇、电压和硬件传感器 | MPL-2.0＋第三方条款 | 可作为可选 Provider，需审查分发 |

## 4. TaskbarWidgets

### 4.1 已观察实现

项目将产品拆为三个隔离部分：

- .NET Loader：配置、Provider、更新、Explorer 恢复；
- Tauri Settings：设置界面；
- Explorer Hook：发现任务栏 XAML 树、布局并渲染状态。

其[架构文档](https://github.com/pfcdev/TaskbarWidgets/blob/main/docs/architecture.md)还采用：

- Provider 分别监督和退避；
- Provider 写独立状态；
- 临时文件后原子替换；
- Hook 不执行 Provider 代码；
- 缺失、畸形、过大或不支持的状态失败关闭；
- 未知命令与协议版本被拒绝；
- Explorer 重启后重新附加。

其[风险文档](https://github.com/pfcdev/TaskbarWidgets/blob/main/docs/windows-private-api-risks.md)明确说明依赖未公开的 XAML Diagnostics 和任务栏视觉树细节，微软更新可修改或删除这些表面。

调研时关键源码入口：

- `src/loader/Core/CommunityProviderSupervisor.cs`
- `src/native/taskbar-hook/`
- `community-sdk/process-runtime.md`
- `docs/architecture.md`
- `docs/windows-private-api-risks.md`

### 4.2 采用

- Provider 每实例/每来源独立监督；
- 快照式 UI 数据；
- 临时文件＋原子替换思想；
- 错误边界失败关闭；
- Explorer 重启恢复；
- 权限和安全等级在安装前显示；
- 未知协议主版本拒绝。

### 4.3 拒绝

- 向 Explorer 注入；
- 依赖私有 XAML 类型名；
- 在任务栏内部放置多个复杂组件；
- 把设置建立在 Tauri/WebView 上；
- 让 Windows 更新兼容性依赖 Hook 适配。

### 4.4 本项目改造

将 Explorer Hook 替换为独立 LauncherHost。保留 Loader/Provider 隔离和状态快照思想。复杂 UI 全部进入 WorkspacePanel。

## 5. WidBar Widget Template

### 5.1 已观察实现

[插件契约](https://github.com/andelby/widbar-widget-template/blob/main/wiki/Plugin-Contract.md)把组件分为：

- Taskbar Preview；
- Flyout；
- Settings。

主要模式：

- `WidgetPluginBase` 提供默认实现；
- `InitializeAsync` 初始化数据；
- `CreatePreviewContent` 创建任务栏 UI；
- `CreateFlyoutContent` 创建扩展窗口；
- `IConfigurableWidgetPlugin` 创建设置；
- 每个放置实例有独立 `InstanceId`；
- 设置以每实例 JSON 保存；
- Flyout 内容创建一次并复用；
- `IWidgetFlyoutLifecycle` 在显示/隐藏时恢复或暂停工作；
- UI 更新回到 UI 线程。

[Preview/Flyout/Settings 文档](https://github.com/andelby/widbar-widget-template/wiki/Preview-Flyout-Settings)强调：

- 隐藏 Preview 时暂停无意义工作；
- Flyout 保温以便快速重开；
- 不在任务栏组件自行绘制任务栏背景；
- 使用系统主题资源；
- 设置取消时恢复原草稿。

### 5.2 采用

- Card Definition 与 Card Instance 分离；
- 每实例设置；
- 视图与生命周期明确；
- Hidden 时暂停轮询但保留长期订阅；
- 设置草稿、预览、提交、取消；
- 主题由宿主统一；
- 卡片声明支持尺寸。

### 5.3 修改

WidBar 允许插件返回任意 WinUI `UIElement`。本项目仅允许内置可信卡片这样做。第三方插件默认返回声明式 UI 和数据，不能进入 WorkspacePanel 进程。

## 6. EarTrumpet

### 6.1 已观察实现

[技术文档](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/README.md)说明：

- WPF 应用包含通知区图标、Flyout 和独立混音器窗口；
- Flyout 在启动时创建并保持可立即显示；
- 设置和混音器窗口按需创建且单实例；
- 直接调用 `Shell_NotifyIcon` 以使用较新的通知区结构；
- 通过 `Shell_NotifyIconGetRect` 等能力确认图标几何；
- 高频音频采样在后台完成，再批量派发到前台；
- 插件按版本目录解析；
- 诊断采用小型环形缓冲区并由用户主动导出。

### 6.2 采用

- Flyout/Panel 预热与按需窗口分离；
- 单实例窗口；
- 后台采样批量提交 UI；
- 系统设置、主题和区域适配；
- 诊断默认留在本地并由用户主动导出；
- 入口几何与窗口生命周期分层。

### 6.3 注意

EarTrumpet 许可证文本为带实体排除条款的 MIT 变体，不应直接当作标准 MIT 处理。当前只参考行为和架构。

## 7. FluentFlyout

### 7.1 已观察实现

[FluentFlyout](https://github.com/unchihugo/FluentFlyout)使用 WPF 构建 Windows 11 风格 Flyout，覆盖：

- Fluent 2 控件；
- Mica；
- 深浅色和系统强调色；
- 平滑动画；
- 可配置弹窗位置；
- 任务栏媒体组件；
- 多显示器。

### 7.2 采用

- 弹窗几何、主题和媒体状态的产品级测试思路；
- 面板位置配置；
- Windows 11 视觉细节；
- 显示器选择；
- 系统 Flyout 不应有多余 chrome。

### 7.3 拒绝

- 不复制 GPL-3.0 源码；
- 不把媒体 Flyout 特化架构扩展为通用工作台；
- 不使用大量设置替代清晰默认值。

## 8. Seelen UI

### 8.1 已观察实现

[Seelen UI](https://github.com/eythaann/Seelen-UI)提供：

- 自定义工具栏、Dock 和桌面组件；
- Shell Flyout；
- 主题和动态强调色；
- Svelte/TypeScript Widget SDK；
- Rust 系统集成；
- 每显示器配置；
- 可移植配置文件。

### 8.2 采用

- 系统状态与 UI 通过 IPC 分离；
- Widget SDK 和主题共享；
- 多显示器独立配置；
- 用户配置可导出；
- 功能模块可单独开启。

### 8.3 拒绝

- 不做完整桌面环境；
- 不替换任务栏、开始菜单、Alt+Tab；
- 不采用常驻 WebView；
- 不采用 AGPL 源码；
- 不把主题自由度置于一致性和可访问性之上。

## 9. Zebar

### 9.1 已观察实现

[Zebar](https://github.com/glzr-io/zebar)使用 Widget Pack：

- `zpack.json` 定义一个包中的多个 Widget；
- GUI 浏览与安装包；
- Provider 向 HTML/CSS/JS UI 提供系统数据；
- 支持任务栏、桌面组件和 Popup；
- 可自动启动单个 Widget；
- 使用原生 WebView，较 Electron 轻但仍有浏览器运行时成本。

### 9.2 采用

- 一个包可包含多个卡片；
- Provider 与 UI 分离；
- Marketplace 元数据独立于运行时；
- 包级版本、资源和权限；
- 卡片可独立启停。

### 9.3 修改

- UI 默认由宿主原生渲染；
- 不把任意 HTML/JS 作为首选插件模式；
- 不使用用户主目录散落配置；
- 权限必须由宿主能力代理执行。

## 10. Rainmeter

### 10.1 采用

- 数据 Measure 与视觉 Meter 分离；
- 配置热重载思想；
- 插件与皮肤分发；
- 低频和事件型数据源；
- 用户可组合多个单一职责组件。

### 10.2 拒绝

- INI 皮肤作为主要用户界面；
- 任意像素桌面布局作为默认；
- 每个 Skin 自行定义完全不同的交互和无障碍；
- 以兼容历史皮肤牺牲现代组件契约。

## 11. TrafficMonitor

### 11.1 已观察实现

[TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)支持：

- CPU、内存、网络和硬件信息；
- 悬浮窗和任务栏显示；
- 皮肤；
- DLL 插件；
- LibreHardwareMonitor；
- Lite 与高级监控分离。

项目文档明确提醒温度等高级监控可能提高 CPU/内存，部分硬件甚至可能导致崩溃或系统问题，因此后续把温度监控迁移到插件。

### 11.2 采用

- 基础监控无管理员权限；
- 高级传感器默认关闭并单独模块化；
- 多网卡选择；
- 每项刷新间隔和显示单位可配置；
- 不支持的硬件明确提示。

### 11.3 拒绝

- 进程内自动加载任意 DLL 插件；
- 让整个应用为温度监控提权；
- 把 `0` 当作不可用值；
- 复制 Anti-996 License 代码；
- 皮肤自由定位替代统一卡片布局。

## 12. ModernFlyouts

仓库已于 2025-11-15 归档，只适合研究：

- Windows Flyout 触发；
- Bridge/Host 分离；
- WPF Fluent 视觉；
- 设置分类。

不得作为新项目依赖或基础分支。

## 13. LibreHardwareMonitor

[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)可读取：

- 主板；
- Intel/AMD CPU；
- NVIDIA/AMD/Intel GPU；
- HDD/SSD/NVMe；
- 网络；
- 温度、风扇、频率、电压和负载。

部分传感器需要管理员权限，不同主板支持差异明显。其主库为 MPL-2.0，并包含第三方许可内容。

采用策略：

- 只在高级硬件 Provider 中使用；
- 默认不启用；
- 单独进程；
- 明确传感器来源、新鲜度和权限；
- 发布前审查 MPL 文件级义务与第三方 Notice；
- 不把高级 Provider 崩溃传播到核心。

## 14. 参考到本项目的映射

| 本项目模块 | 主要参考 | 明确不采用 |
| --- | --- | --- |
| LauncherHost | TaskbarWidgets、EarTrumpet | Explorer 注入、私有 XAML |
| WorkspacePanel | WidBar、FluentFlyout | GPL 源码、任意第三方 XAML |
| 卡片生命周期 | WidBar | 隐藏即销毁、每次重建复杂 UI |
| Provider | TaskbarWidgets、Zebar、Rainmeter | Provider 直接改 UI |
| 系统监控 | TrafficMonitor、LibreHardwareMonitor | 整体提权、进程内不可信 DLL |
| 插件包 | Zebar、WidBar | 默认 full-trust、无权限提示 |
| 主题 | Seelen UI、FluentFlyout | 无限 CSS 覆盖、破坏无障碍 |
| 诊断 | EarTrumpet | 默认上传敏感日志 |

## 15. 代码复用门禁

任何代码复用 PR 必须附：

1. 来源仓库和精确 commit；
2. 来源文件；
3. 许可证；
4. 修改说明；
5. 是否形成衍生作品；
6. NOTICE/源码公开义务；
7. 替代方案；
8. 项目负责人批准。

当前 M0/M1 阶段默认答案是“不复制，只重写”。
