# ADR-0017 验收进程与数据隔离

状态：Accepted
日期：2026-08-10

## 背景

真实桌面 UI Automation 需要启动 WorkspacePanel 和 CoreBroker，并执行布局保存、
便签创建、编辑、删除及进程重启恢复。旧脚本只覆盖了环境变量级隔离：

- 已运行或异常残留的生产 WorkspacePanel/CoreBroker 会占用稳定单实例互斥量；
- 修改子进程 `LOCALAPPDATA` 不能证明
  `Environment.GetFolderPath(LocalApplicationData)` 已改向；
- 一旦隔离假设失效，写入型验收可能访问用户数据库。

生产单实例、默认数据目录和失焦关闭语义均不能为了测试而改变。

## 决策

1. 生产启动继续使用稳定互斥量和
   `%LOCALAPPDATA%\WinWidgetBoard\data.db`，不接受数据目录覆盖。
2. 只有显式带 `--acceptance-test` 的 CoreBroker 才可同时使用：
   - `--test-instance-id <GUID>`：派生验收专用互斥量；
   - `--data-directory <absolute-path>`：指定数据库目录。
3. 验收数据目录必须是系统临时目录的严格子目录。相对路径、临时目录本身、
   目录越界、重复参数和无值参数均拒绝，参数错误退出码为 2。
4. WorkspacePanel 只有在 `--acceptance-test`、`--smoke-test` 或
   `--broker-smoke-test` 下才允许 `--test-instance-id <GUID>` 派生测试互斥量；
   普通启动即使携带该参数也仍使用生产互斥量。
5. `--acceptance-test` 面板可在 UI Automation 切换前台时保持打开；
   生产面板继续遵守激活后失焦关闭和模态保护策略。
6. 验收脚本必须：
   - 为每个 Broker/Panel 生成独立 GUID 和会话令牌；
   - 为所有 `-WithBroker` 路径创建临时数据目录；
   - 只终止脚本持有句柄的子进程；
   - 递归清理前再次确认目标位于系统临时目录内；
   - 不要求终止生产 CoreBroker 或 WorkspacePanel。

## 结果

- 验收实例可与生产实例及其他验收实例并存；
- 写入型 UIA 回归不会依赖用户配置或用户数据库；
- 生产单实例、数据路径和失焦关闭契约保持不变；
- 测试参数属于内部验收接口，不是面向用户的可配置数据路径。

## 被否决方案

- **继续要求手工结束所有旧进程**：异常进程可能拒绝终止，且不能消除数据误用风险。
- **只修改 `LOCALAPPDATA`**：不能作为 .NET 特殊文件夹解析结果的可靠隔离契约。
- **允许任意 `--data-directory`**：扩大生产数据路径攻击面，也增加误删目录风险。
- **为生产实例随机化互斥量**：会破坏既有单实例和 Launcher 交接语义。
