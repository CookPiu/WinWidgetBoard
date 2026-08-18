# 测试目录

当前包含 M1.0 构建基础 smoke、LauncherHost M1.1 几何契约 smoke、WorkspacePanel M1.2 面板几何契约测试、M1.3 输入/动效测试，M2.0.1/M2.0.2/M2.0.3 Contracts、CoreBroker IPC 和面板可见性恢复测试，M2.1.1～M2.1.6 SQLite schema、参数化访问、事务、便签仓储、自动保存服务、便签 IPC、客户端和编辑 ViewModel，M2.4.1 便签复制格式、M2.4.2 保存失败重试、M2.4.3 搜索状态、M2.4.4 搜索结果打开、M2.4.5 编辑撤销/重做和 M2.4.6 Markdown 预览测试，以及 M2.2.1～M2.2.12 响应式网格、布局状态、ItemsRepeater 表面、拖动让位、持久化恢复和编辑会话撤销/重做测试：

```text
tests/
└─ UnitTests/
   ├─ BuildFoundationSmokeTests.cs
   ├─ PanelGeometryTests.cs
   ├─ CardDragControllerTests.cs
   ├─ PanelMotionControllerTests.cs
   ├─ CardReturnMotionControllerTests.cs
   ├─ EnvelopeContractTests.cs
   ├─ LengthPrefixedFrameCodecTests.cs
   ├─ CoreBrokerPipeTests.cs
   ├─ SqliteMigrationTests.cs
   ├─ SqliteStatementTests.cs
   ├─ PersistenceFaultInjectionTests.cs
   ├─ NoteRepositoryTests.cs
   ├─ NoteAutosaveCoordinatorTests.cs
   ├─ NoteIpcTests.cs
   ├─ CoreBrokerNotesClientTests.cs
   ├─ NoteEditorViewModelTests.cs
   ├─ ResponsiveGridLayoutTests.cs
   ├─ CardLayoutReplaySanitizerTests.cs
   ├─ CardLayoutViewModelTests.cs
   ├─ CardLayoutSurfaceViewModelTests.cs
   ├─ CardLayoutEditViewModelTests.cs
   ├─ NoteClipboardFormatterTests.cs
   ├─ NoteSearchViewModelTests.cs
   ├─ NoteEditorNoteSwitchTests.cs
   ├─ NoteEditorUndoTests.cs
   ├─ CardDragPlacementProjectorTests.cs
   └─ CardTemplateDragContractTests.cs
```

`UT-BUILD-001 [BLD-001]` 验证 Contracts 程序集可加载；`UT-PANEL-GEO-001/002` 验证面板几何在触发工作区内、DPI 缩放和无效上下文 fail closed，`UT-PANEL-CONTEXT-001` 验证完整启动上下文解析。测试要求详见 `docs/08-testing-strategy.md`；后续任何任务栏定位、点击穿透、拖拽编排或动效变更都必须增加真实 UI 验证，不能只依赖单元测试。

LauncherHost 还提供 `--geometry-smoke-test`，验证正常、DPI、自动隐藏、异常几何和不可用矩形；`--smoke-test` 会同时验证几何契约与宿主消息窗口启动/退出；`--panel-launch-smoke-test` 验证真实 WorkspacePanel 进程创建和 CLI 上下文传递；`--panel-lifecycle-smoke-test` 验证正常窗口创建、`WM_CLOSE` 和进程退出。WorkspacePanel 的 `--smoke-test` 会构造真实 WinUI `MainWindow` 后退出，`--broker-smoke-test` 会在真实 CoreBroker 进程下完成握手和 `panel.report-visibility` 后退出。它们不能替代真实桌面测试，任务栏位置、透明点击穿透、前台激活、自动隐藏、Explorer 重启、多显示器、面板键盘/失焦交互和拖拽仍需人工验证。
`UT-CARD-DRAG-001/002/003` 覆盖拖动阈值、抓取位移、点击/拖动互斥和取消恢复；这些单元测试不能替代真实鼠标捕获、刷新率和 fall-through 验收。
`UT-PANEL-MOTION-001/002/003` 覆盖面板打开、关闭反向、当前展示值连续性和减少动态效果；它们不能替代真实窗口首帧、失焦、反向和性能验收。
`UT-CARD-MOTION-001/002/003` 覆盖卡片取消回归、回归中断和减少动态效果；它们不能替代真实鼠标捕获、刷新率和 fall-through 验收。
`CT-ENVELOPE-001/002/003/004/005` 覆盖 Envelope 稳定序列化、版本主版本、响应关联、消息大小和畸形 JSON/payload 校验；它们不能替代 Named Pipe、ACL、握手和生命周期测试。
`CT-FRAME-001/002/003/004/005` 覆盖长度帧 round-trip、部分读、零长度、截断和超限；它们不能替代 Named Pipe ACL 和生命周期测试。
`IT-PIPE-001/002/003` 覆盖真实当前用户管道握手、ping、错误 token 拒绝和 CoreBroker 单实例锁；`IT-PIPE-004/005/006` 覆盖 Broker 重启后的客户端重连、周期心跳和静默请求超时；`IT-PIPE-007` 覆盖 WorkspacePanel 会话服务使用共享客户端建立连接；`IT-PIPE-008/009` 覆盖 `panel.report-visibility` 幂等去重、冲突 payload 拒绝和 Broker 重启后的自动重报。它们仍不能替代布局/卡片业务、数据库持久化和完整产品验收。
`IT-PIPE-001/002/003` 覆盖真实当前用户管道握手、ping、错误 token 拒绝和 CoreBroker 单实例锁；`IT-PIPE-004/005/006` 覆盖 Broker 重启后的客户端重连、周期心跳和静默请求超时；`IT-PIPE-007` 覆盖 WorkspacePanel 会话服务使用共享客户端建立连接；`IT-PIPE-008/009` 覆盖 `panel.report-visibility` 幂等去重、冲突 payload 拒绝和 Broker 重启后的自动重报。它们仍不能替代布局/卡片业务、数据库持久化和完整产品验收。
`UT-STORAGE-001～006` 覆盖 schema v1 关系表、幂等迁移、事务回滚、迁移前备份、备份失败保护、foreign key/WAL/内存模式和剪贴板表缺失；`UT-STORAGE-007～015` 覆盖参数化 statement、仓储事务、打开/迁移/写入/提交/备份/恢复故障注入；`UT-NOTE-001～005` 覆盖便签读写、LIKE 转义、revision 冲突和删除保护；`UT-NOTE-006～009` 覆盖编辑 ViewModel 的加载、debounce 自动保存、最新输入胜出、失败后保留内存草稿以及 Broker 不可用时离开加载态；`UT-NOTE-010/011` 覆盖便签复制文本格式和空段处理；`UT-NOTE-012/013` 覆盖失败保存的显式重试、草稿复用和连续失败恢复；`UT-NOTE-014～016` 覆盖搜索结果预览、旧查询取消、错误状态和空查询清理；`UT-NOTE-017～019` 覆盖搜索结果打开、未保存草稿保护和目标缺失保留当前便签；`UT-NOTE-020～022` 覆盖便签编辑撤销、重做、redo 清理和 20 步历史上限；`UT-NOTE-023/024` 覆盖基础 Markdown 块和行内标记预览；`UT-NOTE-025/026` 覆盖 Markdown 便签预览切换和正文格式保存；`IT-NOTE-001～004` 覆盖便签 IPC 及高层客户端的 capability、保存、幂等、搜索、revision 冲突、删除、错误码和输入校验；`UT-GRID-001～006` 覆盖 2/4/6 列、尺寸跨度、确定性 first-fit、窄网格收缩、100 张随机卡片边界、删除紧凑和逻辑单元重放；`UT-GRID-007～016` 覆盖布局 ViewModel、ItemsRepeater 表面、便签状态传播和编辑模式；`UT-GRID-017～021` 覆盖占用单元格让位、越界夹紧、编辑模式投影、placement 提交和取消恢复；`UT-GRID-022～024` 覆盖最终释放落点、边界和持久化顺序；`UT-GRID-025～028` 覆盖计时器/日历让位、四种卡片全网格投影、不替换 ItemsRepeater 对象的原地预览和四模板根表面指针契约；`UT-GRID-029` 覆盖统一入口选择已验证 `x64\Release` 面板产物；`UT-GRID-030/031` 覆盖异常第 7 行回退和边界内留白保留；`UT-GRID-032` 覆盖 placement 顺序变化时卡片表面索引和运行时身份稳定；`UT-GRID-033/034` 覆盖 placement 提交后的网格失效和持久化布局先于首次 repeater 绑定；`UT-GRID-035～038` 覆盖编辑会话移动/缩放/删除撤销重做、redo 栈清理和 20 步历史上限；`UT-GRID-046～049` 覆盖多卡尺寸步进、拖动后缩放的稳定表面索引，以及拖动/缩放从当前 Repeater 元素解析目标。当前 Release 基线为 306/306；这些测试不代表真实鼠标捕获、交互控件隔离、刷新率、DPI、焦点保持或让位动效手感已经完成验收。
LauncherHost 提供 `--corebroker-smoke-test`，在同一会话令牌和真实 CoreBroker 进程下验证原生 `session.hello`/`session.ping`；未设置令牌时应返回专用失败码 19，不输出令牌。

`scripts/Test-CardDragInteraction.ps1` 是受控的真实桌面回归：它拒绝与已有 WorkspacePanel 混用，启动当前 Release x64 面板，等待布局初始化完成，确认拖动起点属于该进程，以标准 `SendInput` 分别从拖动柄和非交互卡片表面执行拖动，核对 UIA 位置与提交状态，最后恢复鼠标位置并关闭自己启动的进程。默认路径覆盖四种卡片的拖动柄、卡面、交互控件隔离和 `Esc` 取消；`-ResizeDragCombination` 额外执行“计时器拖到便签位置→放大→再拖到日历位置”，并验证位置保持及五种模板各一份，成功输出 `REAL-RESIZE-DRAG-PASS`。`-WithBroker` 会启动真实 CoreBroker 并使用隔离临时数据；这些流程仍不替代多显示器、DPI 和刷新率矩阵。

`scripts/Test-CardLayoutHistoryInteraction.ps1` 用 UI Automation 验证编辑模式下的实际缩放、撤销和重做按钮状态；当前参考桌面的按钮流程输出 `REAL-HISTORY-PASS mode-visibility+resize+buttons keyboard-skipped`，键盘快捷键仍需单独拥有前台焦点的流程覆盖。

`scripts/Test-NoteMarkdownPreviewInteraction.ps1` 使用临时 `LOCALAPPDATA`、真实 CoreBroker 和当前 Release x64 面板，验证 Markdown 模式切换、原始正文输入、预览可见性和返回源文按钮；它需要可置前的交互式 Windows 桌面，成功输出 `REAL-NOTE-MARKDOWN-PASS mode+preview`。
