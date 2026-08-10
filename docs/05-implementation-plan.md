# 实施计划

文档状态：已批准基线
版本：0.1
日期：2026-08-06

## 1. 执行原则

- 先验证高风险系统集成，再建设卡片功能。
- 每个里程碑必须有可运行产物和明确退出条件。
- 未达到性能、稳定性或交互门禁时，不扩大范围。
- 每项实现关联需求编号和测试编号。
- Luna 每次只执行一个边界清晰的工作包。
- 不因“以后可能用到”预建大规模框架。

## 2. 里程碑总览

| 里程碑 | 目标 | 预计时间 | 退出条件 |
| --- | --- | ---: | --- |
| M0 | 需求与架构基线 | 已完成 | 文档、ADR、Git 基线 |
| M1 | Launcher＋空面板技术验证 | 2～3周 | 真实 Win11 交互与性能门禁通过 |
| M2 | 网格、存储和核心卡片 MVP | 6～8周 | 核心用户旅程可用 |
| M3 | 稳定性、无障碍和产品化 | 4～6周 | 发布候选通过矩阵 |
| M4 | 账号同步与高级硬件 | 4～8周 | Provider 隔离和数据冲突可控 |
| M5 | 第三方插件 | 6～10周 | 真实沙箱、安全审查和 SDK |

时间为一名熟悉 Windows 桌面开发的工程师估计，Luna 实施仍需人工完成真实 UI 和性能验证。

## 3. M0 — 需求与架构基线

### 3.1 交付物

- [x] PRD；
- [x] UX/动效规范；
- [x] 技术架构；
- [x] 开源项目分析；
- [x] API 契约草案；
- [x] 测试策略；
- [x] 安全与隐私模型；
- [x] ADR；
- [x] Luna 执行指南；
- [x] Git 仓库。

### 3.2 退出条件

- 无已知文档内部冲突；
- 关键待决项被明确列出；
- 稳定版禁用 Explorer 注入；
- MVP 和未来范围区分明确。

## 4. M1 — 技术验证

### 4.1 M1.0 工具链锁定

覆盖工程需求：BLD-001。

任务：

1. 记录当前 Windows、SDK、Visual Studio、.NET 和 Windows App SDK 版本。
2. 创建 `global.json`、中央包版本和传统 `.sln`。
3. 创建 x64 Release/Debug 配置。
4. 建立格式化、静态分析和最小 CI。
5. 只创建 Contracts、LauncherHost、WorkspacePanel、UnitTests。

验证：

- 干净机器或全新用户环境可构建；
- 不需要管理员权限；
- Release 产物路径确定；
- 无未固定的浮动包版本。

提交建议：

```text
build(solution): establish pinned Windows toolchain
```

### 4.2 M1.1 LauncherHost POC

覆盖需求：SYS-001、SYS-003～006、LCH-001～005。

当前工作包：[M1.1 无注入入口 POC](work-packages/M1.1-launcher-entry-poc.md)。

任务：

- 创建无标题、非激活、透明入口窗口；
- 从 monitor/work area 推导任务栏边缘；
- 实现安全位置和 fallback；
- 实现按钮局部命中、其余穿透；
- 处理 pointer-down/up/cancel；
- 处理右键菜单；
- 监听 DPI、显示、设置、Explorer 和电源；
- 输出本地诊断。

禁止：

- 查找或修改任务栏 XAML；
- 注入 Explorer；
- 为测试写注册表持久修改；
- 用固定像素只适配开发机。

测试矩阵：

- 100%、125%、150%、200%；
- 单屏、双屏；
- 居中、左对齐；
- 自动隐藏开/关；
- Explorer 重启；
- 睡眠恢复；
- 全屏应用。

门禁：

- 不覆盖系统按钮；
- 空白像素完全穿透；
- Explorer 重启后恢复；
- 自动隐藏热区不被阻挡；
- LauncherHost 工作集和 CPU 记录达到初步预算。

### 4.2.1 M1.2.1 LauncherHost 到 WorkspacePanel 的启动交接

覆盖需求：SYS-001、LCH-003、PNL-003、PNL-004。

当前工作包：[M1.2.1 进程启动交接](work-packages/M1.2.1-panel-launch-handoff.md)。

任务：

- 使用同用户 `CreateProcessW` 启动 WorkspacePanel，并传递已校验的显示器、工作区、入口矩形和 DPI；
- 已运行时定位并激活面板顶层窗口，关闭时发送 `WM_CLOSE`；
- 轮询进程句柄，面板退出后恢复 LauncherHost 入口状态；
- 提供启动 smoke 和正常窗口生命周期 smoke；
- 保持 LauncherHost 不加载 WinUI、SQLite、HTTP 或插件运行时。

门禁：

- Debug/Release 编译和进程启动/关闭 smoke 通过；
- 缺失面板路径 fail closed，不影响 LauncherHost 自身；
- 真实桌面完成点击、重复打开、关闭、失焦、多显示器/DPI、全屏和 Explorer 重启验收后，才进入 M1.3 动效与输入联调；
- 不把命令行交接当作 IPC 契约，不把 smoke 时间当作 NFR-PERF-004/005 性能结论。

### 4.3 M1.2 WorkspacePanel 空壳

覆盖需求：PNL-001～007、NFR-PERF-004/005。

当前工作包：[M1.2 面板壳层与确定性几何](work-packages/M1.2-panel-shell.md)。

任务：

- WinUI 3 单实例；
- 接收显示器、工作区、入口矩形和 DPI 启动上下文；
- 在正确工作区计算尺寸并从入口所在角锚定；
- 使用无任务栏按钮的独立工具窗口；
- 点击外部/`Esc` 关闭；
- 模态作用域；
- 使用系统主题资源、键盘焦点和资源化无障碍名称；
- 标题区、搜索框、操作按钮、垂直滚动区域和占位卡片；
- 为后续 IPC、卡片运行时和动效保留边界。

本工作包暂以命令行启动上下文验证跨进程数据形状，不在本阶段接入 IPC、数据库或 LauncherHost 调用链。

门禁：

- 纯几何契约验证工作区内、DPI 缩放和无效上下文 fail closed；
- Debug/Release smoke 能构造真实 WinUI `MainWindow` 并正常退出；
- 真实桌面上确认窗口不跨屏、不生成普通任务栏按钮，且外部点击/`Esc` 行为符合预期；
- NFR-PERF-004/005 的按下反馈、首帧和基础可交互时间须在后续真实入口联调中测量；
- 动效中途反向、拖拽和全屏联动转入 M1.3/后续验收。

### 4.4 M1.3 动效与输入 POC

覆盖需求：LCH-003、PNL-003/004、LYT-004、UX 动效规范。

任务：

- 入口按下反馈；
- 面板锚定开关；
- 中途反向；
- 减少动态效果；
- 一张假卡片拖动；
- 8～10px 拖动阈值；
- 指针捕获；
- 抓取偏移；
- 点击/拖动互斥测试。

门禁：

- 真实鼠标交互无 fall-through；
- 动画期间输入不锁定；
- 60Hz 和高刷新率显示器上无明显跳变；
- 减少动态效果无大位移。

已完成工作包：[M1.3.1 单张假卡片拖动 POC](work-packages/M1.3.1-single-card-drag-poc.md)、[M1.3.2 面板开关动效 POC](work-packages/M1.3.2-panel-motion-poc.md) 的当前会话验收。当前工作包：[M1.3.3 单卡拖动取消回归动效 POC](work-packages/M1.3.3-card-return-motion-poc.md)。

### 4.4.1 M1.3.1 单张假卡片拖动 POC

覆盖需求：LCH-003、LYT-004。

任务：

- 实现 8～10 逻辑像素拖动阈值；
- 实现 pointer capture、抓取偏移和 1:1 视觉跟随；
- 区分点击、拖动、取消和捕获丢失；
- 对第一张假卡片提供真实鼠标验证入口；
- 用单元测试覆盖阈值、位移和恢复状态。

门禁：

- 不得同时触发卡片点击和拖动；
- 拖动期间输入不能 fall-through；
- `Esc` 或捕获丢失恢复起始位置；
- 不把单卡片 POC 描述为完整布局引擎。

### 4.4.2 M1.3.2 面板锚定开关与可中断反向 POC

覆盖需求：PNL-003、PNL-004、NFR-PERF-004/005。

任务：

- 从入口所在角计算面板动效起始偏移和变换原点；
- 使用临界阻尼控制器驱动透明度、缩放和位移；
- 统一处理打开、关闭、失焦、`Esc`、关闭按钮和 `WM_CLOSE`；
- 中途触发时从当前展示值和速度反向；
- 减少动态效果只保留淡化反馈；
- 只在动效期间运行 UI 定时器，目标达成后停止。

门禁：

- 真实鼠标交互期间不能锁定输入；
- 打开和关闭路径空间一致，不出现跳变；
- 60Hz 与高刷新率显示器上无明显跳变；
- NFR-PERF-004/005 必须通过真实入口会话测量，不能由单元测试替代。

### 4.4.3 M1.3.3 单卡拖动取消回归动效 POC

覆盖需求：LYT-004。

任务：

- 为 `Esc`、pointer canceled 和 pointer capture lost 增加从当前展示位置回到起点的控制器；
- 使用临界阻尼回归，不在无释放速度的取消路径加入装饰性回弹；
- 新指针按下时从回归动画当前值接管，保持拖动 1:1 和抓取偏移；
- 减少动态效果时直接回到起点；
- 只在回归期间运行共享 UI 定时器，目标达成后停止。

门禁：

- 取消回归不跳变、不 fall-through；
- 回归中重新按下可以立即接管，不等待动画结束；
- 正常拖动释放仍与点击互斥；
- 不把本工作包描述为网格让位、合法落点投影或持久化实现；
- 真实 60Hz 与高刷新率桌面验收通过后，才进入 M2.0 Contracts/CoreBroker 或完整网格工作。

### 4.5 M1 Go/No-Go

M1 结束必须形成决策报告：

| 项目 | Go 条件 |
| --- | --- |
| 入口 | 多场景稳定定位，不覆盖系统 UI |
| 自动隐藏 | 不阻塞系统热区 |
| 面板启动 | 冷/热启动接近性能目标 |
| 动效 | 可中断、点击拖动互斥 |
| 后台资源 | 没有 WinUI 常驻于 Launcher |
| API 边界 | 未使用私有任务栏 XAML |

任一关键项失败时，优先缩减为屏幕边缘入口，而不是改用 Explorer 注入。

## 5. M2 — MVP

### 5.1 M2.0 Contracts 与 CoreBroker

已完成工作包：[M2.0.1 版本化 IPC Envelope 与校验](work-packages/M2.0.1-contract-envelope.md)。当前工作包：[M2.0.2 Named Pipe 当前用户传输与握手 POC](work-packages/M2.0.2-named-pipe-handshake.md)。

任务：

- 定义协议 Envelope；
- Named Pipe ACL；
- 握手、版本、大小、超时；
- CoreBroker 单实例；
- Launcher/Panel 客户端；
- 心跳和重连；
- correlation ID；
- Contract tests。

门禁：

- 畸形 JSON、超大消息、未知版本均被拒绝；
- CoreBroker 重启后客户端可恢复；
- 敏感字段不写日志。

### 5.1.1 M2.0.1 版本化 IPC Envelope 与校验

覆盖需求：G-006、SYS-001、NFR-REL-001。

任务：

- 定义 `request`、`response`、`event` 共用的版本化 Envelope；
- 固定 camelCase JSON、UTC 时间、UUID 标识和 CorrelationId 语义；
- 在反序列化前拒绝空消息和超过 1MiB 的消息；
- 拒绝主版本不兼容、未知消息类型、缺少响应关联、非对象 payload 和超长错误字段；
- 提供可复用的 UTF-8 JSON 编解码入口和契约测试；
- 提供 4 字节 little-endian 长度帧编解码，并正确处理部分读和截断；
- 生成 `src/Contracts/Schemas/envelope.schema.json`，为后续命名管道实现提供同一结构基线。

门禁：

- 有效 Envelope 可稳定序列化和反序列化；
- 畸形 JSON、超大消息、未知主版本和无关联响应均被拒绝；
- 测试工程通过 Contracts 程序集引用，不重复编译协议源文件；
- 本工作包不宣称 Named Pipe ACL、握手、心跳、重连或 CoreBroker 已完成。

### 5.1.2 M2.0.2 Named Pipe 当前用户传输与握手 POC

覆盖需求：G-006、SYS-001、NFR-REL-001。

任务：

- 创建独立 CoreBroker Console/Worker 入口和本用户单实例 mutex；
- 使用 `PipeOptions.CurrentUserOnly` 创建异步 byte-mode Named Pipe；
- 复用 M2.0.1 长度帧与 Envelope，首条请求处理 `session.hello`；
- 校验 session token、clientType、x64 架构、进程 ID 和协议范围；
- 握手后提供 `session.ping`，验证当前连接仍可收发；
- 将 Named Pipe 客户端放入可被 UI 进程复用的 `CoreBroker.Client` 程序集；
- LauncherHost 使用无 UI/无第三方 JSON 依赖的原生客户端完成 session 保活；
- 客户端为连接和请求设置超时，并提供周期心跳；
- 客户端在断管、请求超时和 Broker 重启窗口执行一次重连，心跳任务对瞬时连接错误按周期继续尝试；
- WorkspacePanel 通过独立会话服务异步连接 CoreBroker，不把 IPC 业务逻辑写入 XAML code-behind；
- 提供真实进程 `--pipe-handshake-smoke-test` 和集成测试。

门禁：

- 当前用户可连接，其他用户/提升级别不因默认配置获得访问权；
- 错误 token、未知客户端类型和不兼容协议范围返回稳定错误码；
- 重复 CoreBroker 实例被拒绝；
- 关闭和取消不留下 pipe、任务或 CoreBroker smoke 进程；
- 客户端请求超时、周期心跳、断管重连和 Broker 重启窗口恢复由集成测试覆盖；
- Launcher/Panel 生产接入、幂等命令恢复和完整 CoreBroker 重启恢复保留到后续工作包。

### 5.1.3 M2.0.3 面板可见性业务方法与重连恢复

覆盖需求：PNL-004、G-006、NFR-REL-001。

任务：

- 定义 `panel.report-visibility` 请求/响应和 `clientOperationId`；
- 采用状态设置语义，禁止用 toggle 语义承载可重试命令；
- CoreBroker 对相同 operation 和 payload 返回相同业务结果，对冲突 payload 拒绝，并限制内存去重表大小；
- WorkspacePanel 首次会话建立后上报可见性；心跳发现 Broker 重连后重新上报最新目标状态；
- 提供真实 WinUI `--broker-smoke-test`，覆盖握手、业务上报和清理。

门禁：

- 相同 `clientOperationId` 的重复请求不重复应用状态；
- 同一 ID 搭配不同 payload 返回 `validation.invalid-argument`；
- Broker 重启后客户端可以重新握手并安全设置目标状态；
- Debug/Release 33/33 测试、WorkspacePanel 构建和真实进程 smoke 通过；
- 不引入数据库或把进程内 revision 描述为跨重启持久化。

### 5.2 M2.1 持久化

当前工作包：[M2.1.6 NTE-001 WorkspacePanel 便签编辑器](work-packages/M2.1.6-note-panel-editor.md) 已实现；M2.1.1 交付 schema/migration/backup 基础，M2.1.2 交付通用参数化访问、事务和隔离恢复故障注入，M2.1.3 交付便签持久化读写和 revision 冲突保护，M2.1.4 交付 UI 无关的 debounce 自动保存服务，M2.1.5 交付便签 IPC，M2.1.6 交付单便签 UI 接入，仍不包含便签完整能力、布局或其他领域业务。

任务：

- SQLite schema v1；
- migration runner；
- 布局、卡片实例、便签、待办、日历、计时器；
- WAL 和单写入队列；
- 原子 launcher/boot JSON；
- 备份与只读恢复；
- 导入导出 v1。

M2.1.6 验收重点：

- WorkspacePanel 通过 `CoreBroker.Client` 加载和保存单条便签；
- 输入 debounce 且只提交最新草稿；
- 保存失败保留内存草稿并显示错误状态；
- 编辑区不抢占演示卡片拖拽区域；
- 真实桌面输入和失焦关闭仍需人工验证。

门禁：

- 进程在写入中终止后数据库可恢复；
- 迁移失败保留旧数据库；
- 剪贴板正文不进入数据库；
- 测试覆盖 migration up/failed/retry。

### 5.3 M2.2 网格布局

覆盖需求：LYT-001～007。

已完成工作包：[M2.2.1 LYT-001/002/005 响应式网格核心](work-packages/M2.2.1-responsive-grid-engine.md)、[M2.2.2 LYT-001/002/005 卡片布局 ViewModel](work-packages/M2.2.2-card-layout-viewmodel.md)、[M2.2.3 LYT-001/002/005 ItemsRepeater 卡片视觉接入](work-packages/M2.2.3-items-repeater-card-surface.md)、[M2.2.4 LYT-003 布局编辑模式与卡片操作门禁](work-packages/M2.2.4-layout-edit-mode.md)、[M2.2.5 LYT-004 拖动落点投影核心](work-packages/M2.2.5-drag-placement-projection.md)、[M2.2.6 LYT-003/004 真实指针拖动与释放提交](work-packages/M2.2.6-pointer-drag-commit.md)、[M2.2.7 LYT-004 拖动落点与视觉状态修复](work-packages/M2.2.7-drag-drop-boundary-fix.md)、[M2.2.8 LYT-006 卡片布局持久化](work-packages/M2.2.8-layout-persistence.md)、[M2.2.9 LYT-006 布局逻辑位置重放修复](work-packages/M2.2.9-layout-position-replay.md)、[M2.2.10 LYT-003/004/005 卡片拖动入口与实时让位修复](work-packages/M2.2.10-card-drag-reflow-preview.md)、[M2.2.11 LYT-004/005/DAT-001 持久化空行与拖动视觉恢复](work-packages/M2.2.11-bounded-layout-gap-recovery.md)、[M2.2.12 LYT-006 布局撤销与重做](work-packages/M2.2.12-layout-undo-redo.md)。当前已具备纯布局函数、可测试状态层、ItemsRepeater 视觉接入、编辑模式门禁、统一卡片拖动入口、占用单元格实时让位、指针释放提交、CoreBroker/SQLite 布局保存、逻辑位置重放、异常大空行的有界恢复和编辑会话撤销/重做；让位弹簧补间、虚拟化回收和完整可访问性验收仍未实现。

任务：

- 纯布局函数；
- 2/4/6 列；
- S/M/L/W/XL；
- deterministic packing；
- ItemsRepeater/虚拟化；
- 编辑模式；
- 拖动、缩放、删除；
- 撤销/重做；
- 持久化。

门禁：

- 100 张随机卡片属性测试无重叠/越界；
- 相同输入输出一致；
- 拖动真实交互通过；
- 卡片删除后紧凑稳定；
- 无每帧数据库写入。

### 5.4 M2.3 统一卡片运行时

任务：

- Definition/Instance；
- 生命周期；
- Ready/Loading/Stale/Error 等状态；
- 快照订阅；
- 可见性调度；
- 设置草稿；
- 错误边界；
- 示例卡片。

门禁：

- 一张卡片异常不影响其他卡片；
- Hidden 后停止 UI 工作；
- 再显示时数据恢复；
- 未知状态有安全降级。

### 5.5 M2.4 内置卡片批次 A

当前已完成工作包：[M2.4.1 NTE-001 便签显式复制动作](work-packages/M2.4.1-note-copy-action.md)、[M2.4.2 NTE-001 便签保存失败显式重试](work-packages/M2.4.2-note-save-retry.md)；便签搜索、恢复、Markdown 最小集和多便签管理仍待完成。

#### 便签

- 自动保存；
- 保存状态；
- Markdown 最小集；
- 搜索；
- 恢复；
- 显式复制；
- 保存失败后的显式重试；
- 输入期间失焦策略。

#### 计时器

- 持久目标时间；
- 暂停/恢复；
- 通知；
- 重启恢复；
- 多计时器可推迟。

#### 本地待办

- CRUD；
- 排序；
- 截止日期；
- 完成；
- 删除撤销。

门禁：

- 离线完全可用；
- 进程重启数据不丢；
- 键盘核心流程通过。

### 5.6 M2.5 内置卡片批次 B

#### 天气

- Provider abstraction；
- 手动城市；
- 缓存；
- 退避；
- Stale/Offline。

#### 剪贴板

- 当前内容；
- 系统历史；
- 文本/图片/文件安全预览；
- 暂停；
- 不持久化；
- 大内容限制。

#### 基础系统监控

- CPU/内存/网络/磁盘/电池；
- 统一调度；
- 可见图表；
- 面板隐藏降频。

#### 本地日历

- 月视图；
- 本地事件；
- 提醒；
- 时区。

### 5.7 M2.6 设置、隐私和诊断

任务：

- 设置分类；
- 入口预览和位置编辑；
- 主题、透明度、动效；
- 卡片管理；
- 隐私总览；
- 通知；
- 数据导入导出；
- 诊断包；
- 恢复默认。

门禁：

- 取消视觉设置恢复原值；
- 危险操作明确确认；
- 导出不含令牌和剪贴板正文；
- 设置可键盘操作。

### 5.8 M2 退出条件

- MVP 七类卡片可用；
- 核心用户旅程完成；
- 无管理员权限；
- 多显示器和 DPI 基本通过；
- 性能达到或有明确差距报告；
- 无 P0/P1 已知缺陷；
- 文档与状态同步。

## 6. M3 — 产品化

### 6.1 稳定性

- 24/72 小时 soak；
- Explorer 多次重启；
- 睡眠/唤醒循环；
- 显示器热插拔；
- 数据库故障注入；
- CoreBroker/Panel 崩溃恢复；
- 安装、升级、卸载、回滚。

### 6.2 无障碍

- UI Automation 树；
- Narrator；
- 键盘；
- 高对比度；
- 文本缩放；
- 减少动态效果；
- 减少透明度。

### 6.3 性能

- 启动 ETW；
- 空闲功耗；
- 工作集；
- 图表与滚动帧时间；
- 图片缓存；
- 长期内存趋势。

### 6.4 分发

- MSIX；
- 代码签名；
- 稳定/预览通道；
- 隐私声明；
- 第三方 Notice；
- 崩溃报告手动导出；
- 更新和回滚。

## 7. M4 — Provider 扩展

### 7.1 Outlook 日历

- Entra 应用注册；
- 最小权限；
- OAuth；
- Credential Manager；
- Calendar View delta；
- etag 和冲突；
- 账号解绑。

### 7.2 Microsoft To Do

- Todo lists/tasks；
- 增量或高效同步；
- 本地映射；
- 删除 tombstone；
- 冲突 UI。

### 7.3 Google 日历

独立 Provider 和授权，不与 Outlook 实现耦合。

### 7.4 高级硬件

- LibreHardwareMonitor 适配；
- 兼容性清单；
- 可选辅助组件；
- 权限提示；
- 崩溃隔离；
- 许可证 Notice。

## 8. M5 — 第三方插件

必须在单独安全设计评审后开始。

### 8.1 前置硬门禁

- 选定真实沙箱；
- 完成威胁模型；
- 权限可被技术执行；
- 包签名/哈希；
- 安装 UI；
- 卸载和数据删除；
- API 兼容策略；
- 插件崩溃/超时/资源限制测试。

### 8.2 SDK

- manifest schema；
- provider contract；
- declarative UI schema；
- sample cards；
- validator CLI；
- packaging CLI；
- test host；
- API docs；
- compatibility tests。

## 9. 工作包模板

Luna 每次任务必须按以下格式定义：

```text
目标：
关联需求：
允许修改：
禁止修改：
前置条件：
实现步骤：
验证命令：
人工验证：
性能影响：
安全/隐私影响：
完成证据：
```

单个工作包建议：

- 修改不超过 3 个主要模块；
- 1～3 个提交；
- 可在一个开发会话完成；
- 不混合重构和新功能；
- 有明确回滚路径。

## 10. Git 计划

### 10.1 分支

- `main`：始终可构建；
- `feature/<scope>-<short-name>`；
- `fix/<scope>-<short-name>`；
- `docs/<short-name>`；
- `spike/<short-name>`：仅技术验证，不直接合并生产代码。

### 10.2 提交

示例：

```text
feat(launcher): add per-monitor taskbar geometry resolver
test(layout): cover deterministic collision compaction
perf(panel): suspend hidden composition updates
docs(adr): reject explorer xaml injection
```

### 10.3 PR 门禁

- 需求编号；
- 变更范围；
- 测试；
- 人工 UI 证据；
- 性能影响；
- 安全/隐私；
- 截图或录屏仅在 UI 变化时；
- 文档更新；
- 无许可证问题。

## 11. 风险登记

| ID | 风险 | 概率 | 影响 | 负责人动作 |
| --- | --- | --- | --- | --- |
| R-001 | 无注入入口无法稳定覆盖目标位置 | 中 | 高 | M1 POC，准备 EdgeTab fallback |
| R-002 | WinUI 冷启动达不到 300ms | 中 | 高 | Boot Snapshot、保温、依赖裁剪 |
| R-003 | 自动隐藏被入口阻塞 | 中 | 高 | 真实输入测试，隐藏时不占边缘 |
| R-004 | 拖动与点击冲突 | 中 | 中 | 阈值、pointer capture、UI 回归 |
| R-005 | 卡片过多导致内存高 | 高 | 高 | 虚拟化、生命周期、上限测试 |
| R-006 | 剪贴板造成隐私泄露 | 中 | 极高 | 默认不持久化、遮罩、威胁测试 |
| R-007 | 插件权限无法真实执行 | 高 | 极高 | 插件延期，先选沙箱 |
| R-008 | 高级硬件组件不稳定 | 中 | 高 | 可选进程、默认关闭、兼容清单 |
| R-009 | 第三方许可证污染 | 中 | 高 | 依赖清单、复用门禁 |
| R-010 | Luna 扩大范围或跳过验证 | 中 | 高 | AGENTS、工作包、状态文档和提交门禁 |

## 12. Luna 与人工分工

Luna 适合：

- 创建项目结构；
- 实现纯布局算法；
- 编写 DTO、序列化和数据库迁移；
- 单元、契约和故障注入测试；
- 实现明确接口下的卡片；
- 维护文档与状态。

必须人工或真实桌面会话验证：

- 任务栏位置；
- 点击穿透；
- 自动隐藏；
- Explorer 重启；
- 多显示器；
- 高 DPI；
- 动效手感；
- 输入法；
- 屏幕阅读器；
- WPR/WPA 性能；
- UAC、安全桌面和安装。
