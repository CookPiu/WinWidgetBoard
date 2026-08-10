# 源码目录

M1.1 当前已为 LauncherHost 增加无注入入口 POC；M1.2 已为 WorkspacePanel 增加面板壳层和确定性启动几何；M1.3.1 已接入第一张假卡片的拖动输入 POC；M1.3.2 已接入面板锚定开关动效 POC；M1.3.3 已接入第一张假卡片的取消回归动效；M2.0.1 已接入 Contracts 协议 Envelope、JSON Schema 和长度帧校验；M2.0.2 已接入 CoreBroker 当前用户 Named Pipe、共享客户端、握手、客户端超时/心跳/基础重连和单实例 POC：

```text
src/
├─ LauncherHost/       # C++/Win32 几何解析与透明入口 POC
├─ WorkspacePanel/     # C#/.NET 10/WinUI 3 面板壳层、启动上下文与几何计算
├─ CoreBroker.Client/  # C#/.NET 10 跨 UI 进程复用的 Named Pipe 客户端
├─ CoreBroker/         # C#/.NET 10 当前用户 Named Pipe、session 握手与 SQLite 存储基础
└─ Contracts/          # 版本化 JSON/长度帧契约程序集
```

`PluginHost`、`Cards.BuiltIn` 和 `Packaging` 尚未创建。M1.1 入口 POC 本身不负责面板交接；M1.2.1 已增加独立的同用户进程启动、关闭和状态轮询 POC；M1.3.1 只对一张占位卡片提供阈值、捕获、抓取偏移和点击/拖动互斥；M1.3.2 将位移应用到原生面板窗口、将缩放应用到 RootGrid，并将透明度同步到原生窗口表面（不支持时回退到 XAML 透明度），统一处理关闭和可中断反向；M1.3.3 为取消拖动提供从当前视觉位置回到起点的可中断控制器；M2.0.1 提供版本化 Envelope、稳定 JSON 编解码、Schema 和长度帧；M2.0.2 提供当前用户 Named Pipe、共享客户端、session.hello/session.ping、客户端超时/心跳/基础重连和 CoreBroker 单实例，WorkspacePanel 与 LauncherHost 已接入后台/原生保活客户端；M2.1.1 提供 Windows 11 系统 `winsqlite3.dll` 的 schema/migration/backup 基础，M2.1.2 增加参数化 statement、事务、通用仓储边界和恢复故障注入，M2.1.3 增加 NTE-001 便签仓储与 revision 冲突保护，M2.1.4 增加 UI 无关的 debounce 自动保存服务层，M2.1.5 增加 notes.save/get/search/delete CoreBroker IPC，M2.1.6 增加 `CoreBrokerNotesClient`、可测试 `NoteEditorViewModel` 和 WorkspacePanel 单便签编辑区，M2.4.1 增加便签显式复制动作，M2.4.2 增加保存失败后的显式重试和内存草稿保留，M2.4.3 增加便签搜索结果展示，M2.4.4 增加从搜索结果安全打开便签，但尚未完成便签完整能力或其他领域业务。后续实现必须按 `docs/05-implementation-plan.md` 和对应工作包推进。

M2.2.1 已增加独立的 `WorkspacePanel/Layout/ResponsiveGridLayout` 纯布局核心，M2.2.2 已增加 `CardLayoutViewModel` 作为响应式卡片状态层，M2.2.3 已接入 `CardLayoutSurfaceViewModel`、`CardGridLayout` 和 ItemsRepeater，M2.2.4 已加入 `CardLayoutEditViewModel` 编辑模式门禁，M2.2.5 已加入 `CardDragPlacementProjector`，M2.2.6～M2.2.7 已接入指针拖动、placement 提交和边界修复，M2.2.8～M2.2.9 已接入 CoreBroker/SQLite 持久化与逻辑单元重放，M2.2.10 已统一四种卡片根表面的拖动入口并接入占用单元格的实时让位预览，M2.2.11 已加入 `CardLayoutReplaySanitizer`、布局完成后首次绑定和 placement 提交后的网格失效，M2.2.12 已加入编辑会话布局撤销/重做；让位弹簧补间、虚拟化回收和完整可访问性验收仍待完成。
