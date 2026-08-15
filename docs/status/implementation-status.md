# 实施状态

最后更新：2026-08-15

## 当前阶段

`M2.2 / M2.4 当前代码范围与 M2.3.0～M2.3.7 已收口（M2.2.13 / M2.4.11 已完成当前单显示器真实桌面验收、验收进程/数据隔离和自动回归；M2.3.1 已完成 Definition/Instance、生命周期、不可变快照、订阅和实例级错误边界；M2.3.2 已完成面板/ItemsRepeater 实现视口驱动的 Hidden/Visible、集中式本地快照调度、取消和迟到结果门禁；M2.3.3 已完成 UI 无关的设置草稿、预览取消恢复和提交门禁；M2.3.4 已完成十态纯投影、五模板共享状态区、双语资源、静态加载骨架和未建模动作门禁；M2.3.5 已完成 CoreBroker 内 Scheduled Provider 纯 C# 合同、集中刷新、退避、手动限流、状态暂停和有界释放核心；M2.3.6 已完成唯一生产 pump host、fatal fault 监督、CardStateSnapshot 结果适配和有界事件缓冲；M2.3.7 已完成 cards.subscribe 连接级初始/异步事件传输、客户端响应分流、慢客户端断开和 WorkspacePanel dispatcher 适配；设置 UI/IPC/持久化、真实 Provider/HTTP/IPC/缓存、M1.0 干净环境与 CI、真实多显示器/DPI/完整无障碍与性能矩阵、便签版本历史、删除撤销、其他领域 CRUD、剩余卡片业务和正式数据库恢复演练仍待完成）`

## 已完成

- [x] 确认产品定位：体验级替代 Windows 11 Widgets。
- [x] 确认稳定版本不注入 Explorer。
- [x] 确认任务栏入口与半屏工作台分进程。
- [x] 确认本地优先、无资讯流、无强制账号。
- [x] 确认以 Apple 风格的即时、锚定、可中断动效作为交互原则。
- [x] 完成主要开源项目调研。
- [x] 建立文档结构。

## M1.0 已实现并通过的本地验证

- [x] 建立仅包含 Contracts、LauncherHost、WorkspacePanel 和 UnitTests 的传统解决方案。
- [x] 锁定 .NET SDK、Windows App SDK 组件、Windows SDK、MSVC 和测试依赖版本。
- [x] WorkspacePanel 与 UnitTests 的 locked-mode restore 通过。
- [x] Contracts、UnitTests 和 WorkspacePanel 的 Debug/Release x64 构建均为 0 警告、0 错误。
- [x] LauncherHost 使用 Visual Studio MSBuild 完成 Debug/Release x64 构建。
- [x] `UT-BUILD-001 [BLD-001]` 在 Debug 和 Release 各通过 1 次。
- [x] LauncherHost 与 WorkspacePanel 的 Debug/Release `--smoke-test` 均返回 0。
- [x] WorkspacePanel Debug/Release smoke 已实际构造真实 `MainWindow`，标题资源和面板 XAML 初始化通过，并以退出码 0 结束。
- [x] LauncherHost Release 动态依赖中无 WinUI、SQLite、HTTP 或插件运行时。
- [x] WorkspacePanel 依赖图已收敛为 Runtime、WinUI 与必要传递组件，不含 AI、ML、Widgets 或 DWrite。
- [x] XML/JSON、双语资源键、解决方案项目、禁入依赖、尾随空白和 `git diff --check` 检查通过。

## M1.0 尚未通过的门禁

- [ ] 在系统安装 .NET SDK `10.0.302` 的干净环境完成完整 `.sln` Debug/Release x64 构建。
- [ ] 运行并通过 GitHub Actions。
- [ ] 人工目视确认 WorkspacePanel M1.2 面板壳层的内容、布局和交互；自动化启动/关闭探针已通过。

本机系统路径没有安装 .NET SDK。本次使用校验过的官方便携 SDK 完成托管验证；便携 SDK 未注册到 Visual Studio SDK resolver，因此不能把分项目构建结果表述为完整 `.sln` 或干净 CI 已通过。

## M1.1 入口 POC 已实现但尚未完成真实验收

- [x] 使用公开 Win32/工作区矩形推导任务栏边缘，不读取任务栏私有 XAML。
- [x] 实现工作区安全边缘位置、自动隐藏/异常几何 fallback 和最小 32×32 逻辑像素命中区域。
- [x] 实现无标题、无任务栏按钮、非激活 layered popup，以及按钮局部命中和透明区域穿透契约。
- [x] 实现 pointer capture 的按下/释放取消、右键菜单和入口状态反馈。
- [x] 监听 `TaskbarCreated`、显示/DPI/设置/电源消息，并实现重定位与基础全屏隐藏。
- [x] `--geometry-smoke-test` 覆盖正常、DPI、自动隐藏、异常几何和不可用矩形。
- [ ] 在真实 Windows 11 桌面完成任务栏位置、点击穿透、DPI、多显示器、自动隐藏、全屏和 Explorer 重启验收。
- [ ] 记录 LauncherHost 的 CPU、工作集和唤醒性能基线。
- [x] 当前真实交互会话已验证入口不被普通最大化窗口遮挡，点击桌面不会误触发全屏隐藏，入口点击可打开面板。

## M1.2 面板壳层已实现但尚未完成真实验收

- [x] 通过 `PanelLaunchContext` 接收并校验显示器、工作区、入口矩形和 DPI；
- [x] 通过 `PanelGeometry` 计算工作区内尺寸、入口角锚定和无效几何 fail closed；
- [x] 创建单实例、无普通任务栏按钮的 WinUI 3 工具窗口，并设置无标题栏 presenter；
- [x] 实现基础面板外失焦关闭、`Esc` 关闭和可嵌套模态作用域；
- [x] 实现标题/日期/上下文、搜索框、添加/编辑/设置/关闭操作、垂直滚动区和四个占位卡片；
- [x] 双语资源、系统主题资源、键盘焦点和 AutomationProperties 名称已加入；
- [x] `UT-PANEL-GEO-001/002`、`UT-PANEL-CONTEXT-001` 与 Debug/Release WinUI smoke 已通过；
- [ ] 在真实 Windows 11 桌面确认可见布局、键盘焦点、面板外点击、模态失焦保护和多显示器/DPI 行为；
- [ ] 测量 NFR-PERF-004/005 的按下反馈、首帧和基础可交互时间；
- [x] 完成 LauncherHost 到 WorkspacePanel 的最小同用户进程启动交接 POC；真实点击联动待验收；

## M1.2.1 进程启动交接已实现但尚未完成真实验收

- [x] 通过 `CreateProcessW` 传递显示器、工作区、入口矩形和 DPI 启动上下文；
- [x] 已运行面板时定位顶层窗口并尝试激活，关闭时发送公开 `WM_CLOSE`；
- [x] 通过进程句柄轮询面板退出并恢复 LauncherHost 入口状态；
- [x] `--panel-launch-smoke-test` 与 `--panel-lifecycle-smoke-test` 的 Debug/Release 组合均返回 0；
- [x] 面板路径缺失时 fail closed，返回专用退出码 18；
- [x] 当前真实交互会话已验证入口点击、重复打开/激活、关闭和失焦；
- [ ] 在完整 Windows 11 矩阵中继续验证多显示器/DPI、全屏和 Explorer 重启；
- [ ] 测量从入口释放到面板首帧和基础可交互的 NFR-PERF-004/005 数据。

## M1.3 与后续

- [x] M1.3.1 单张假卡片拖动与点击互斥 POC 的控制器、WorkspacePanel 接入和自动测试；
- [x] M1.3.1 已通过当前真实 Windows 11 交互会话的阈值、拖动、捕获丢失和取消验收；
- [x] M1.3.2 面板锚定打开、统一关闭路径、可中断反向、减少动态效果，以及原生窗口背景透明度同步的控制器与 WorkspacePanel 接入；
- [x] M1.3.2 当前真实 Windows 11 会话已通过入口展开、统一关闭、反向、减少动态效果和 60Hz 目视验收；完整显示器矩阵与 NFR-PERF-004/005 定量记录仍待完成；
- [x] M1.3.3 单卡取消回归控制器、回归中断接管、减少动态效果分支与自动测试；
- [x] M1.3.3 当前真实 Windows 11 会话已通过取消回归、捕获丢失、回归中断、减少动态效果和刷新率验收；
- [x] M2.0.1 Contracts 已加入版本化 Envelope、稳定 JSON 编解码、4 字节 little-endian 长度帧、主版本/消息类型/关联 ID/payload/大小校验和契约测试；
- [x] M2.0.2 CoreBroker 已加入当前用户专用 Named Pipe、session.hello/session.ping、token/协议范围校验和 Local 单实例锁；
- [x] M2.0.2 客户端已加入请求超时、周期心跳、断管重连 API，并补充 IT-PIPE-004/005/006/007 回归测试；
- [x] M2.0.2 已拆出 `CoreBroker.Client` 共享程序集，WorkspacePanel 通过 `CoreBrokerSession` 异步握手并运行心跳；缺少令牌或 Broker 不可用时不阻断面板启动；
- [x] M2.0.2 LauncherHost 已加入无第三方依赖的原生 `session.hello`/`session.ping` 客户端和 `--corebroker-smoke-test`；
- [x] LauncherHost 已用锁定 MSVC 14.44 完成 Debug/Release 直接编译，无令牌降级和临时合法管道握手/ping smoke 通过；
- [x] M2.0.2/M2.0.3 已使用校验过的便携 .NET SDK `10.0.302`（含 .NET/WindowsDesktop Runtime `10.0.10`）完成 Debug/Release 新增回归测试，33/33 通过；CoreBroker、CoreBroker.Client 和 WorkspacePanel 构建均为 0 警告、0 错误；
- [x] M2.0.2 已完成真实 Release CoreBroker + Release LauncherHost 进程 smoke，同一会话令牌下 `session.hello`/`session.ping` 返回成功；无令牌回归退出码仍为 19；
- [x] M2.0.3 已实现 `panel.report-visibility`、`clientOperationId` 有界去重和 WorkspacePanel Broker 重启后自动重新上报；IT-PIPE-008/009、真实 WorkspacePanel `--broker-smoke-test` 和原生 LauncherHost 重试 smoke 已通过；
- [x] M2.1.1 已加入可注入路径的 SQLite 数据库入口、schema v1、`schema_migrations`、事务化幂等迁移、迁移前备份和备份失败保护；不创建剪贴板历史表或持久化剪贴板正文；
- [x] M2.1.1 已在当前 Windows 11 的系统 `winsqlite3.dll` 上完成 Debug/Release 分项目构建和 39/39 单元测试；覆盖外键、WAL/内存模式、回滚、幂等和备份边界；
- [x] M2.1.2 已加入参数化 `SqliteStatement`、事务/通用仓储边界、打开/迁移/写入/提交/备份/恢复故障注入和隔离恢复副本；Debug/Release 均为 48/48 单元测试通过；
- [x] M2.1.3 已加入 NTE-001 `NoteRepository`，支持便签创建、读取、列表、搜索、更新、删除和 `updated_at_utc` revision 冲突保护；Debug/Release 均为 53/53 单元测试通过；
- [x] M2.1.4 已加入 UI 无关的 `NoteAutosaveCoordinator`，支持 debounce、最新输入优先、取消和保存失败保留内存草稿；Debug/Release 均为 56/56 单元测试通过；
- [x] M2.1.5 已加入 `notes.save/get/search/delete` CoreBroker IPC、写操作幂等缓存、输入长度/格式校验和生产 CoreBroker 本地数据库初始化；Debug/Release 均为 58/58 单元测试通过；
- [x] M2.1.6 已加入 `CoreBrokerNotesClient`、`NoteEditorViewModel` 和 WorkspacePanel 单便签标题/正文编辑区；保存采用 debounce、revision token 和失败草稿保留策略；并修复 CoreBroker 不可用时永久停留在加载态的问题；Debug/Release 均为 64/64 测试通过，WorkspacePanel 构建 0 警告、0 错误，WinUI Debug/Release smoke 均返回 0；用户已确认真实 CoreBroker + WorkspacePanel 便签加载、保存、重开恢复和编辑/拖拽交互成功；
- [ ] M2.1.1 的干净网络 restore、完整 `.sln`/CI 和跨 Windows 11 构建矩阵仍待完成；当前 restore 使用缓存包并记录 NuGet 源不可访问警告；
- [x] M2.2.1 已加入响应式网格纯函数、S/M/L/W/XL 尺寸映射、2/4/6 列选择、确定性 first-fit 排布和 100 张随机卡片属性测试；后续 M2.2.13 已补齐视口实现边界和当前单屏真实布局回归；
- [x] M2.2.2 已加入 `CardLayoutViewModel`，提供响应式列数切换、只读 Items/Placements 快照、卡片增删改尺寸、确定性重排和非法输入原子失败保护；Debug/Release 均为 75/75，WorkspacePanel 构建 0 警告、0 错误，WinUI smoke 均返回 0；ItemsRepeater 和真实卡片交互仍待接入；
- [x] M2.2.3 已将 `CardLayoutSurfaceViewModel`、模板选择器和 `CardGridLayout` 接入 WorkspacePanel ItemsRepeater，四张演示卡片按 placement 快照响应式排布；后续 M2.2.13 已根据 `RealizationRect` 实现视口内卡片并完成真实四卡交互；
- [x] M2.2.4 已加入 `CardLayoutEditViewModel` 和显式编辑模式门禁；普通模式拒绝卡片缩放/删除/演示拖动，编辑模式支持尺寸操作、移除、Esc 取消恢复快照和完成提交；Debug/Release 均为 80/80，WorkspacePanel 构建 0 警告、0 错误，WinUI smoke 均返回 0；真实鼠标交互和持久化仍待接入；
- [x] M2.2.5 已加入 `CardDragPlacementProjector` 和编辑模式下的 `TryPreviewDrop`，支持尺寸感知边界夹紧、最近合法落点和无重叠确定性投影；Debug/Release 均为 83/83，WorkspacePanel 构建 0 警告、0 错误，WinUI smoke 均返回 0；真实指针接入和落点提交仍待完成；
- [x] M2.2.6 已将拖动把手、pointer capture、1:1 卡片变换和释放 placement 提交接入 ItemsRepeater；后续工作包已补齐实时让位、真实四卡交互和布局持久化重启回归；
- [x] M2.2.7 已修复拖动提交后旧 `CompositeTransform` 被 ItemsRepeater 回收复用、释放点使用旧 `PointerMoved` 落点以及网格列边界夹紧问题；当前桌面连续四卡回归已通过，完整边缘释放矩阵仍待完成；
- [x] M2.2.8 已接入 `layout.get/layout.save`、SQLite 布局仓储、revision/幂等保护、启动加载和完成编辑保存，并修复完成按钮状态切换、卡片顺序、第二轮编辑偏移和 Broker 启动竞态；当前隔离桌面已通过保存、Broker/Panel 重启和第二轮编辑；
- [x] M2.2.9 已修复便签动态状态被 `x:Uid` 静态文本覆盖的问题，并通过 SQLite schema v2、`preferredRow` 契约和当前 placement 行列快照恢复布局空位；当前桌面重启恢复和第二轮编辑已通过，完整旧 schema 真实迁移矩阵仍待完成；
- [x] M2.2.10 已统一便签、计时器、待办和本地日历的卡片根表面拖动入口，将占用单元格投影改为确定性让位，并以不替换 ItemsRepeater 数据源的方式接入实时预览；M2.2.13 已补齐四卡拖动柄/非交互表面、交互控件隔离和 `Esc` 取消真实回归；逐帧动效、边缘阻力和完整显示矩阵仍待验收；
- [x] M2.2.11 已根据当前数据库 revision 18、`demo.timer.preferred_row = 7` 的复现证据，增加异常持久化空行的有界内存恢复；同时修复异步加载前默认模板抢先实现、卡片身份顺序变化和 placement 提交后自定义网格未重新排列的问题；Debug/Release 均为 107/107，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误；真实 Broker + 当前数据库及无 Broker 默认布局两条路径均通过计时器/日历拖动柄和卡片表面双向换位并输出 `REAL-DRAG-PASS`；测试未点击“完成”，只读复查数据库仍为 revision 18、第 7 行，证明没有启动时静默写库；
- [x] M2.2.12 已为显式布局编辑会话加入最多 20 步的移动/缩放/删除撤销与重做，新增操作清空 redo 栈，完成或取消编辑清空历史，并接入标题区按钮与 `Ctrl+Z`/`Ctrl+Y`；当前桌面已通过按钮和快捷键真实回归；屏幕阅读器和文本框快捷键优先级仍待人工验收；
- [x] M2.4.1 已为 NTE-001 便签卡片加入显式复制按钮，复制标题和正文原文到 Windows 当前剪贴板，不写入应用数据库；`UT-NOTE-010/011` 覆盖格式化和空段处理，Release UnitTests 113/113，WorkspacePanel Release x64 构建 0 警告、0 错误，WinUI smoke 返回 0；用户已确认真实桌面剪贴板验证成功；
- [x] M2.4.2 已为 NTE-001 便签保存失败增加显式重试入口，复用内存草稿且不做无界自动重试；当前隔离桌面已通过 Broker 故障、显式重试和重启读取回归；
- [x] M2.4.3 已将标题区搜索框接入 `notes.search`，增加 debounce、旧查询取消、结果预览和错误/空查询状态；当前隔离桌面已通过最新查询胜出、旧结果消失和结果打开回归；
- [x] M2.4.4 已将搜索结果接入便签编辑器安全加载，未保存草稿或保存进行中时阻止切换，目标不存在时保留当前便签；当前隔离桌面已通过搜索打开和未保存草稿门禁；
- [x] M2.4.5 已为便签标题/正文增加最多 20 步显式撤销与重做，复用自动保存且新编辑清空 redo；当前隔离桌面已通过撤销、重做、保存和重启恢复；
- [x] M2.4.6 已为 NTE-001 便签增加 Markdown/纯文本模式、基础块级预览和源文往返切换，保存请求保留原始正文并携带正确正文格式；当前隔离桌面已通过模式、预览和源文往返回归；
- [x] M2.4.7 已为 NTE-001 增加最后一次已提交状态恢复回归；当前隔离桌面已通过 WorkspacePanel/CoreBroker 终止、重启和已提交状态读取，不新增历史表、IPC 方法或键盘移动功能；
- [x] M2.4.8 已为 NTE-001 增加“全部”入口和多便签只读列表，复用 `notes.search("")` 和草稿保护的安全导航；当前隔离桌面已通过列表与已保存便签回归；
- [x] M2.4.9 已为 NTE-001 增加新建便签并安全切换，复用现有 `notes.save` 创建契约生成 `note-*` ID；当前隔离桌面已通过新建、列表、搜索、打开和草稿保护回归；
- [x] M2.4.10 已为 NTE-001 增加多便签安全编辑与删除，复用 `notes.delete` revision、确认和草稿保护；修复删除其他便签后默认便签重载短路，当前隔离桌面已通过当前/列表删除和安全切换回归；
- [x] M2.2.13 / M2.4.11 已完成桌面验收收口：CardGrid 视口实现、四卡稳定 UIA 锚点、便签固定高度内部滚动、验收实例/临时数据库隔离；Debug/Release UnitTests 163/163，WorkspacePanel/CoreBroker Debug/Release x64 构建 0 警告、0 错误，WinUI smoke 退出码 0；当前单显示器真实桌面通过四卡拖动、布局历史、2/4/6 列、布局重启恢复和 5 条便签 UIA 链路，生产数据库三张表只读哈希与基线一致；
- [x] M2.3.0 已建立 WorkspacePanel UI 基础视觉层：应用级四像素间距与圆角 Token、系统主题画刷、标题/命令分层、共享卡片/输入/状态/编辑工具样式；Debug/Release UnitTests 167/167，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误，WinUI smoke 退出码 0；DPI 192 普通态/编辑态截图、四卡真实拖拽、布局按钮与 `Ctrl+Z`/`Ctrl+Y`、便签新建链路均通过；完整暗色/高对比度/文本缩放/多显示器矩阵和 M2.3 卡片运行时仍待后续；
- [x] M2.3.1 已建立统一卡片 Definition/Instance、六态生命周期、不可变快照与订阅、单实例刷新错误边界和内置四卡类型目录；现有卡片表面按类型 ID 选择模板，便签 Loading/Ready/Unavailable/Error 经适配器同步，未知状态不暴露推断动作并使用无业务按钮的安全模板；Debug/Release UnitTests 180/180，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误，Release WinUI smoke 退出码 0，隔离 Broker 下四卡双入口拖动、控件隔离和 `Esc` 取消输出 `REAL-DRAG-PASS`；M2.3.2 已补可见性与本地快照调度，M2.3.3 已补设置草稿，M2.3.4 已补通用状态视觉，真实 Provider 周期/退避仍待后续；
- [x] M2.3.2 已建立单一事件驱动 `CardRuntimeVisibilityScheduler`，将面板开关和 ItemsRepeater 实现/回收接入卡片 Hidden/Visible，Hidden 取消纯 UI 快照刷新，恢复 Visible 请求一次当前本地快照；generation、runtime identity 和 commit gate 拒绝隐藏、注销或释放后的迟到 Ready/Error，在途刷新完成等待共享全局 250ms 预算且不创建每卡 Timer/循环；Debug/Release UnitTests 198/198，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误，Release WinUI smoke 退出码 0，隔离 Broker 下四卡双入口拖动、控件隔离和 `Esc` 取消输出 `REAL-DRAG-PASS`；严格像素级 EffectiveViewport、真实 Provider 周期/退避/节能和性能矩阵仍待后续；
- [x] M2.3.3 已建立 UI 无关的卡片设置草稿合同：不可变 JSON 快照保留未知字段并限制为 64 KiB，schema 区分 `PreviewSafe`/`CommitOnly`，取消按首次触碰逆序恢复且聚合单项失败，提交以 session/draft version/expected revision 拒绝旧请求并只接受 `expected + 1`；Debug/Release UnitTests 224/224，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误，Release WinUI smoke 退出码 0；本包未接入设置 UI、IPC、数据库或真实外部副作用；
- [x] M2.3.4 已建立 UI 无关的十态 `CardRuntimeStatusPresentation`，将 Ready+Stale、显式 Stale、Offline、Error、Unknown 和既有 AllowedActionIds 投影为本地化状态、内容可见性与安全动作门禁；五种卡片模板复用静态状态区，计时器/待办/日历未建模主动作已移除。Debug/Release UnitTests 237/237，WorkspacePanel Debug/Release x64 构建 0 警告、0 错误，Release WinUI smoke 退出码 0；当前无 Broker 的真实 UIA 已读取四个可见 Unavailable 状态锚点并正常关闭，隔离 Broker 下既有四卡拖拽输出 `REAL-DRAG-PASS`。Loading/Stale/Error 的真实 Provider 状态切换、动作执行、双语运行时切换和完整无障碍/显示矩阵仍未验收；
- [x] M2.3.5 已在 CoreBroker 内建立 Scheduled Provider 纯 C# 集中调度核心：稳定 request key 合并与 fan-out、注入单调时钟和 jitter、visible/hidden/power cadence、timeout、Retry-After、指数退避、手动刷新门禁、network/power/visibility 暂停、generation/registration identity 迟到拒绝、每 key 有界前驱和共享 Dispose 预算；Debug/Release UnitTests 265/265，CoreBroker 与 WorkspacePanel Debug/Release x64 构建均为 0 警告、0 错误，Release WinUI smoke 退出码 0。该 E1 证据仅覆盖内存 fake 和现有 UI 回归，不代表生产宿主循环、真实 Provider/HTTP/IPC/SQLite、系统电源/网络事件或 NFR 性能指标完成；
- [x] M2.3.6 已接入 CoreBroker 唯一 `ProviderRefreshHost` pump 生命周期、`ProviderProcessFatalSupervisor` 单次 fatal fault 监督、`CardsContract`/`CardStateSnapshot` 共享合同、Provider 结果到状态/freshness/sequence/动作的纯适配，以及按 instance ID 合并的有界事件缓冲；CT-CARD-001/002 与 UT-CARD-099/100/101/102/103/104 共 8 项新增测试通过，全量 UnitTests Debug/Release 均为 273/273，CoreBroker 与 WorkspacePanel Debug/Release x64 构建均为 0 警告、0 错误。证据等级为 E1/E2；真实 Provider/HTTP/IPC/SQLite 数据流、系统网络/电源事件和 NFR 性能指标仍未完成；
- [x] M2.3.7 已接通连接级 `cards.subscribe`/`cards.snapshot` Named Pipe 事件传输：CoreBroker 按连接维护可见性订阅和最新快照，命令响应与异步 Event 共用有序写锁，客户端读取泵分流响应/事件，32 槽有界按实例合并队列 overflow 时断开慢客户端，并提供 WorkspacePanel `CardSnapshotDispatcher` 的 UI 调度注入和 sequence/type/schema 门禁；`UT-CARDS-001/002/003` 新增 3 项，全量 UnitTests Debug 为 276/276，CoreBroker、CoreBroker.Client 和 WorkspacePanel Debug 构建均为 0 警告、0 错误。证据等级为 E1/E2，断线后需重新订阅，真实 Provider/HTTP/IPC/SQLite 数据流、系统网络/电源事件和 NFR 性能指标仍未完成；
- [ ] M2.1 便签完整能力、其他领域 CRUD、M2.2 剩余布局/卡片业务和活动数据库恢复；
- [ ] CoreBroker 与 IPC。
- [ ] 内置卡片。
- [ ] 安装、更新与签名。

## 待用户决策

1. 正式产品名称与图标。
2. 最终项目许可证。
3. 首发是否只支持 Windows 11 x64。
4. 首发天气数据源。
5. Outlook/Google 日历同步的优先级。
6. 是否在首发版本开放第三方插件安装。

## 下一步

M2.3.7 已冻结并实现连接范围内的 `cards.subscribe`/`cards.snapshot` 事件传输、慢客户端断开和 WorkspacePanel dispatcher 适配。下一工作包应在首发天气数据源、网络权限、缓存和隐私范围确认后选择一个真实 Provider 接入；在决策完成前不接入真实 HTTP，也不把内存 fake 描述为真实 Provider。以下保留累计路线与完成记录：

1. 在安装锁定 SDK 的干净环境或 CI 执行完整 `.sln` Debug/Release x64 构建；
2. 人工目视验证 WorkspacePanel M1.2 面板壳层的内容、布局和交互；
3. 完成 M1.0 验收记录；
4. 按 [M1.1 入口工作包](../work-packages/M1.1-launcher-entry-poc.md) 完成真实 Windows 11 验收；
5. 记录性能基线并决定是否继续扩大入口范围；
6. 按 [M1.2 面板壳层工作包](../work-packages/M1.2-panel-shell.md) 完成真实桌面验收和 NFR-PERF-004/005 测量；
7. 按 [M1.2.1 进程启动交接工作包](../work-packages/M1.2.1-panel-launch-handoff.md) 完成真实点击联动、重复打开和生命周期验收；
8. 复核 [M2.0.1 Envelope 工作包](../work-packages/M2.0.1-contract-envelope.md) 的协议程序集、JSON Schema 和契约测试证据；
9. [x] 已在便携锁定 SDK 环境完成 M2.0.2 新增 IT-PIPE-004/005/006/007、Debug/Release 构建和 CoreBroker 冒烟；
10. [x] 已验证 `CoreBroker.Client`、WorkspacePanel 会话服务和 Debug/Release UI smoke；
11. [x] 已按 [M2.0.2 Named Pipe 工作包](../work-packages/M2.0.2-named-pipe-handshake.md) 完成 LauncherHost/CoreBroker 真实进程 smoke；
12. [x] 已按 [M2.0.3 面板可见性工作包](../work-packages/M2.0.3-panel-visibility-recovery.md) 完成首个业务方法、幂等重试和 Broker 重启安全重报；下一步进入 M2.1 持久化与布局/卡片业务。
13. [x] 已按 [M2.1.1 SQLite 存储基础工作包](../work-packages/M2.1.1-storage-foundation.md) 完成 schema v1、迁移事务和迁移前备份；
14. [x] 已按 [M2.1.2 数据仓储基础与恢复故障注入工作包](../work-packages/M2.1.2-repository-and-recovery-fault-injection.md) 完成通用参数化访问、事务接线、确定性故障注入和隔离恢复副本；下一步再拆分领域 CRUD 与活动数据库恢复策略。
15. [x] 已按 [M2.1.3 NTE-001 便签持久化仓储工作包](../work-packages/M2.1.3-note-repository.md) 完成便签持久化 CRUD、搜索和 revision 冲突保护；下一步实现便签 UI/自动保存调度或其他领域仓储。
16. [x] 已按 [M2.1.4 NTE-001 便签自动保存服务层工作包](../work-packages/M2.1.4-note-autosave-coordinator.md) 完成 debounce 自动保存、最新输入优先和失败草稿保留；下一步接入便签 UI/IPC 或实现其他领域仓储。
17. [x] 已按 [M2.1.5 NTE-001 便签 CoreBroker IPC 工作包](../work-packages/M2.1.5-note-ipc.md) 完成便签契约、当前用户 IPC、幂等写入和输入校验；下一步接入 WorkspacePanel 便签 UI或实现其他领域仓储。
18. [x] 已按 [M2.1.6 NTE-001 WorkspacePanel 便签编辑器工作包](../work-packages/M2.1.6-note-panel-editor.md) 接入高层客户端、可测试 ViewModel 和最小便签编辑区，并修复 Broker 不可用时的加载态卡死；Debug/Release 64/64、构建和 WinUI smoke 已通过；下一步补齐便签完整能力或进入布局/卡片业务。
19. [x] 已按 [M2.2.1 LYT-001/002/005 响应式网格核心工作包](../work-packages/M2.2.1-responsive-grid-engine.md) 完成纯布局函数、尺寸映射、确定性排布和属性测试；
20. [x] 已按 [M2.2.2 LYT-001/002/005 卡片布局 ViewModel 工作包](../work-packages/M2.2.2-card-layout-viewmodel.md) 完成布局状态层、响应式重排、增删改尺寸和失败输入保护；下一步接入 ItemsRepeater，并补充真实拖动落位验收。
21. [x] 已按 [M2.2.3 LYT-001/002/005 ItemsRepeater 卡片视觉接入工作包](../work-packages/M2.2.3-items-repeater-card-surface.md) 完成视觉数据快照、模板选择和响应式网格排布；下一步实现虚拟化回收、编辑模式和真实拖动落位验收。
22. [x] 已按 [M2.2.4 LYT-003 布局编辑模式与卡片操作门禁工作包](../work-packages/M2.2.4-layout-edit-mode.md) 完成显式编辑模式、尺寸/移除门禁、Esc 快照恢复和拖动入口门禁；下一步实现真实拖动落位、虚拟化回收和撤销/重做。
23. [x] 已按 [M2.2.5 LYT-004 拖动落点投影核心工作包](../work-packages/M2.2.5-drag-placement-projection.md) 完成最近合法网格落点、越界夹紧和编辑模式投影门禁；下一步接入真实指针预览与释放提交。
24. [x] 已按 [M2.2.6 LYT-003/004 真实指针拖动与释放提交工作包](../work-packages/M2.2.6-pointer-drag-commit.md) 接入拖动把手、pointer capture、1:1 变换、取消回归和合法 placement 提交；下一步补实时让位预览、真实桌面验收和撤销/重做。
25. [x] 已按 [M2.2.7 LYT-004 拖动落点与视觉状态修复工作包](../work-packages/M2.2.7-drag-drop-boundary-fix.md) 修复错误吸附、边界越界和回收后卡片消失问题；下一步补实时让位预览、真实桌面回归验收和撤销/重做。
26. [x] 已按 [M2.2.8 LYT-006 卡片布局持久化工作包](../work-packages/M2.2.8-layout-persistence.md) 接入 CoreBroker/SQLite 布局保存和启动加载，并完成首轮保存反馈及卡片顺序稳定性修复；下一步完成真实重启恢复验收、实时让位预览和撤销/重做。
27. [x] 已按 [M2.2.9 LYT-006 布局逻辑位置重放修复工作包](../work-packages/M2.2.9-layout-position-replay.md) 接入 `preferredRow`、schema v2 迁移和空位重放，并修复便签动态状态覆盖；下一步完成真实桌面迁移、保存和重开验收。
28. [x] 已按 [M2.2.10 LYT-003/004/005 卡片拖动入口与实时让位修复工作包](../work-packages/M2.2.10-card-drag-reflow-preview.md) 统一四种卡片拖动入口、实现占用单元格确定性让位和 ItemsRepeater 原地预览；M2.2.13 已补齐四卡双入口、控件隔离与 `Esc` 取消回归，显示/性能矩阵和让位动效仍待后续。
29. [x] 已按 [M2.2.11 LYT-004/005/DAT-001 持久化空行与拖动视觉恢复工作包](../work-packages/M2.2.11-bounded-layout-gap-recovery.md) 完成异常第 7 行的有界内存恢复、布局完成后首次绑定和提交后的网格失效，并在真实 Broker + 当前数据库及无 Broker 默认布局下完成双入口拖动回归；下一步由用户点击“完成”保存恢复位置，再补齐便签/待办与显示矩阵。
30. [x] 已按 [M2.2.12 LYT-006 布局撤销与重做工作包](../work-packages/M2.2.12-layout-undo-redo.md) 完成编辑会话 20 步历史、撤销/重做按钮和 `Ctrl+Z`/`Ctrl+Y` 接入，并通过当前桌面按钮/快捷键回归；屏幕阅读器和完整焦点矩阵待后续。
31. [x] 已按 [M2.4.1 NTE-001 便签显式复制动作工作包](../work-packages/M2.4.1-note-copy-action.md) 完成复制格式化器、双语按钮和剪贴板写入，并完成真实桌面剪贴板验证；
32. [x] 已按 [M2.4.2 NTE-001 便签保存失败显式重试工作包](../work-packages/M2.4.2-note-save-retry.md) 完成失败草稿保留、显式重试和连续失败恢复状态，并通过当前桌面 Broker 故障/恢复回归。
33. [x] 已按 [M2.4.3 NTE-001 便签搜索结果展示工作包](../work-packages/M2.4.3-note-search-results.md) 完成搜索状态层、标题区结果列表和双语状态，并通过当前桌面搜索回归。
34. [x] 已按 [M2.4.4 NTE-001 从搜索结果打开便签工作包](../work-packages/M2.4.4-note-search-open.md) 完成安全切换、草稿保护和缺失目标保留，并通过当前桌面打开/草稿门禁回归。
35. [x] 已按 [M2.4.5 NTE-001 便签编辑撤销与重做工作包](../work-packages/M2.4.5-note-undo-redo.md) 完成文本快照历史、撤销/重做按钮和自动保存接线，并通过当前桌面恢复回归。
36. [x] 已按 [M2.4.6 NTE-001 便签基础 Markdown 预览工作包](../work-packages/M2.4.6-note-markdown-preview.md) 完成模式切换、基础预览、保存格式和拖动控件隔离接线，并通过当前桌面模式/预览/往返回归。
37. [x] 已按 [M2.4.7 NTE-001 便签已提交状态恢复回归工作包](../work-packages/M2.4.7-note-committed-state-recovery.md) 增加文件型 SQLite 重开验证和真实进程重启 UIA，并通过当前桌面重启恢复；版本历史仍需独立需求契约。
38. [x] 已按 [M2.4.8 NTE-001 多便签只读列表与安全导航工作包](../work-packages/M2.4.8-note-list-navigation.md) 增加空查询列表、全部入口和草稿保护导航，并通过当前桌面列表回归。
39. [x] 已按 [M2.4.9 NTE-001 新建便签并安全切换工作包](../work-packages/M2.4.9-note-create-switch.md) 增加新建按钮、客户端唯一 ID 和失败保留，并通过当前桌面新建/搜索/草稿保护回归。
40. [x] 已按 [M2.4.10 NTE-001 多便签删除与安全编辑工作包](../work-packages/M2.4.10-note-delete-edit.md) 增加删除确认、revision 保护、草稿门禁和安全切换，并通过当前桌面当前/列表删除回归。
41. [x] 已按 [M2.2.13 / M2.4.11 桌面验收与隔离收口工作包](../work-packages/M2.2.13-M2.4.11-desktop-acceptance-closeout.md) 完成视口实现、UIA 可达性、验收进程/数据隔离、当前单屏真实交互和生产数据只读复核；下一步回到完整硬件/无障碍/性能矩阵及剩余产品范围。
42. [x] 已按 [M2.3.0 UI 基础样式与共享卡片外壳工作包](../work-packages/M2.3.0-ui-visual-foundation.md) 建立共享视觉 Token、系统主题样式和普通态/编辑态层级，并通过当前 DPI 192 截图、四卡拖拽、布局历史与便签 UIA 回归；下一步在不回退该视觉契约的前提下拆分 M2.3 统一卡片运行时。
43. [x] 已按 [M2.3.1 CRD-001/002/005 统一卡片运行时基础工作包](../work-packages/M2.3.1-card-runtime-foundation.md) 建立卡片定义、实例、生命周期、不可变快照、订阅、错误隔离和未知卡片安全模板，并通过 180/180 自动测试、Debug/Release 构建、WinUI smoke 与当前隔离四卡真实拖动回归；下一步进入 M2.3.2 可见性与快照调度。
44. [x] 已按 [M2.3.2 CRD-003 可见性与快照调度工作包](../work-packages/M2.3.2-card-visibility-snapshot-scheduling.md) 建立集中式 panel/viewport 生命周期与本地快照调度、取消、迟到 Ready/Error 门禁和有界释放，并通过 198/198 自动测试、Debug/Release 构建、WinUI smoke 与当前隔离四卡真实拖动回归；随后已拆分并完成 M2.3.3 设置草稿与取消恢复，真实 Provider 周期/退避另行实施。
45. [x] 已按 [M2.3.3 CRD-004/SET-003 卡片设置草稿与取消恢复工作包](../work-packages/M2.3.3-card-settings-draft.md) 建立不可变设置快照、声明式写入策略、预览取消恢复、64 KiB 边界和两阶段提交门禁，并通过 224/224 自动测试、WorkspacePanel Debug/Release 构建与 Release WinUI smoke；本包无设置 UI、IPC 或数据库变更，随后已完成 M2.3.4 通用状态视觉与示例快照。
46. [x] 已按 [M2.3.4 CRD-001/002/005 通用卡片状态视觉与示例快照工作包](../work-packages/M2.3.4-card-runtime-state-visuals.md) 建立十态纯投影、五模板共享状态区、双语主题资源、静态加载骨架和未建模动作门禁，并通过 237/237 自动测试、WorkspacePanel Debug/Release 构建、Release smoke、当前 Unavailable 状态 UIA 与隔离四卡拖拽回归；真实 Provider 状态切换、动作执行和完整无障碍/显示矩阵仍待后续。
47. [x] 已按 [M2.3.5 CRD-001/002/003/005 集中 Provider 刷新调度核心工作包](../work-packages/M2.3.5-provider-refresh-scheduler-core.md) 在 CoreBroker 内建立 Scheduled Provider 合同、同源合并/fan-out、确定性 cadence/timeout/Retry-After/退避/手动限流、状态暂停、迟到拒绝和有界释放，并通过 Debug/Release 265/265 自动测试、CoreBroker/WorkspacePanel 双配置构建和 Release smoke；证据等级为 E1 纯内存 fake，生产宿主、HTTP、IPC、缓存、系统事件和性能指标仍待后续。
48. [x] 已按 [M2.3.6 Provider 生产宿主与卡片快照边界工作包](../work-packages/M2.3.6-provider-host-and-card-snapshot-boundary.md) 接入唯一生产 pump host、fatal fault 监督、共享 CardStateSnapshot 合同、Provider 结果适配和有界事件缓冲，并通过新增 8 项合同/宿主/适配器测试、Debug/Release 全量 273/273 和 CoreBroker/WorkspacePanel Debug/Release 构建；证据等级为 E1/E2，真实 Provider、HTTP、缓存、系统事件和性能指标仍待后续。
49. [x] 已按 [M2.3.7 cards.subscribe 实时事件传输工作包](../work-packages/M2.3.7-cards-subscribe-event-transport.md) 接通连接级初始/异步快照、客户端响应分流、有界 overflow 断开和 WorkspacePanel dispatcher 适配，并通过 `UT-CARDS-001/002/003`、Debug UnitTests 276/276 及 CoreBroker/CoreBroker.Client/WorkspacePanel Debug 构建；断线后重新订阅和真实 Provider/HTTP/缓存/系统事件/性能指标仍待后续。
