# ADR-0013：WorkspacePanel 便签客户端与编辑器边界

状态：Accepted
日期：2026-08-08
关联需求：NTE-001、DAT-001、NFR-REL-002
替代：无

## 背景

M2.1.5 已完成便签 CoreBroker IPC，但面板仍只有占位卡片。若让 XAML code-behind 直接组装 Envelope 或打开 SQLite，会破坏 `WorkspacePanel`、`CoreBroker.Client` 和 `CoreBroker` 的边界，也会让自动保存难以测试。

## 决策

1. `CoreBroker.Client` 提供 `INoteClient` 和 `CoreBrokerNotesClient`，负责构造便签请求、重连发送、响应反序列化和错误码保留。
2. `WorkspacePanel` 只依赖客户端接口；`NoteEditorViewModel` 负责加载、单便签 debounce 自动保存、revision token 传递和失败草稿保留。
3. XAML code-behind 只处理控件事件、焦点和本地化状态显示，不包含数据库或 IPC 业务规则。
4. 编辑器与演示卡片拖拽面分离，文本输入不会触发卡片拖拽捕获。
5. Broker 不可用时面板仍可启动；编辑器显示不可用状态，不伪造已保存结果。

## 后果

- 便签高层调用和编辑器行为可以在不启动 WinUI 的单元/集成测试中验证；
- 保存失败会保留当前 ViewModel 草稿和错误状态，下一次输入可继续触发保存；
- 本工作包只接入单个便签编辑器；Markdown 选择、搜索、删除和完整撤销/复制验收由后续便签工作包逐项交付，M2.4.6 已在不改变客户端边界的前提下加入 Markdown 模式和基础预览；不得据此宣称 NTE-001 全部完成；
- 真实鼠标输入、面板失焦关闭和跨进程重启后的 UI 旅程仍需桌面验收。
