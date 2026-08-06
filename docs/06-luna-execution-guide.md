# Luna 实施指南

文档状态：已批准基线
目标读者：后续负责实现的 Luna 模型
版本：0.1

## 1. 你的角色

你是 WinWidgetBoard 的实施代理，不是产品需求的重新设计者。你的职责是：

- 在批准的需求和 ADR 范围内实现；
- 保持现有结构；
- 用测试和真实证据验证；
- 发现矛盾时停止并报告；
- 保持每次改动小、清晰、可回滚。

不要因为模型能够生成大量代码，就一次性生成完整应用。该项目最危险的部分是 Windows 任务栏、输入、窗口层级、性能和隐私，必须分阶段验证。

## 2. 每次任务的强制阅读顺序

1. 根目录 `AGENTS.md`；
2. `README.md`；
3. `docs/status/implementation-status.md`；
4. `docs/01-product-requirements.md` 中关联需求；
5. `docs/03-technical-architecture.md` 中关联模块；
6. `docs/02-ux-design-spec.md` 中关联交互；
7. `docs/08-testing-strategy.md` 中关联验证；
8. 相关 ADR；
9. 当前 `git status` 和 `git log -5 --oneline`。

若没有明确需求编号，不开始编码。

## 3. 绝对约束

### 3.1 任务栏

稳定实现不得：

- 注入 `explorer.exe`；
- 使用 XAML Diagnostics 修改任务栏；
- 依赖私有任务栏元素名称；
- 假设固定任务栏高度；
- 用固定坐标覆盖系统按钮；
- 阻塞自动隐藏任务栏热区。

遇到公开 API 无法可靠定位时，使用 `EdgeTabFallback` 或报告阻塞，不得私自改用 Hook。

### 3.2 性能

- LauncherHost 不得引用 WinUI、SQLite、HTTP、图片处理或插件运行时。
- WorkspacePanel 隐藏后停止 Composition 更新和 UI 计时器。
- Provider 使用统一调度器和 CancellationToken。
- 不允许每张卡片创建永久高频 Timer。
- 不在拖动每一帧写数据库。
- 不在 UI 线程做网络、数据库、大图解码和硬件枚举。

### 3.3 安全与隐私

- 不记录剪贴板正文、OAuth 令牌、插件密钥和完整用户路径。
- 剪贴板默认不持久化。
- 不把 Job Object 描述成安全沙箱。
- 不让整个应用请求管理员权限。
- 不自动修改 Widgets 组策略、注册表或用户系统偏好。
- 外部输入全部验证。

### 3.4 许可证

- 未经批准不复制参考项目源码。
- 引入 NuGet、vcpkg、源码包或资源前更新开源参考/Notice。
- GPL、AGPL、Anti-996 和带额外限制的代码不得直接进入本项目。
- 不使用来源不明的图标、字体、图片或动画。

## 4. 每个工作包的执行流程

### 4.1 定义

先输出或写入任务说明：

```text
目标：
需求编号：
ADR：
允许修改：
禁止修改：
假设：
验收标准：
自动验证：
人工验证：
风险：
```

若用户已经给出明确范围，直接按范围执行；若关键决策缺失且会改变架构、数据或验收，先询问。

### 4.2 检查

执行前：

```powershell
git status --short
git log -5 --oneline
```

- 保留用户未提交更改；
- 不重置、不覆盖；
- 不顺手修改无关格式；
- 检查目录下是否有更具体的 `AGENTS.md`。

### 4.3 计划

计划必须：

- 3～7 个可验证步骤；
- 一次只有一个进行中步骤；
- 把真实 UI 验证单列；
- 把文档/状态更新列为最后步骤；
- 不把“运行测试”写成模糊一句。

### 4.4 实现

- 先写最小失败测试或可复现 harness；
- 实现最小修改；
- 运行局部测试；
- 处理错误路径；
- 再运行上层验证；
- 不在同一提交混合无关重构。

### 4.5 交付

最终报告必须包含：

- 实现结果；
- 修改文件；
- 需求编号；
- 已运行命令和结果；
- 未运行验证及原因；
- 性能/安全/隐私影响；
- Git 状态和提交；
- 下一步。

禁止把“代码能编译”写成“功能已在 Windows 任务栏验证”。

## 5. 编码规范

### 5.1 C++/LauncherHost

- 使用 C++23；
- RAII 管理 HWND 相关资源、HANDLE、注册消息和 Hook；
- 优先 WIL 或项目自有窄封装，新增依赖前审查；
- Unicode API；
- 不使用裸 `new/delete`；
- Win32 错误保留错误码和上下文；
- 窗口过程只做轻量分派；
- 几何类型明确逻辑像素、物理像素和屏幕坐标；
- 每显示器 DPI 感知在进程启动最早阶段设置；
- 失败时隐藏入口而不是使用危险默认位置。

命名示例：

```text
TaskbarGeometryResolver
LauncherWindow
DisplayTopologyWatcher
FullscreenPolicy
PanelClient
```

### 5.2 C#/.NET

- .NET 10、nullable enabled；
- async 方法接收 CancellationToken；
- 不使用 `async void`，事件处理器除外；
- 业务逻辑不放在 code-behind；
- ViewModel 不直接访问 SQLite、HTTP 或 Windows 敏感 API；
- 使用依赖注入，但避免过度抽象；
- 记录稳定错误码；
- `IDisposable/IAsyncDisposable` 生命周期明确；
- 序列化 DTO 与数据库实体分离；
- 时间持久化使用 UTC 和明确时区字段。

命名示例：

```text
PanelSessionService
CardLayoutEngine
CardRuntime
ProviderScheduler
ClipboardProvider
TimerCoordinator
```

### 5.3 XAML/WinUI

- 使用 ThemeResource；
- 不硬编码系统色；
- 页面业务状态通过 ViewModel；
- 控件包含 AutomationProperties；
- 焦点顺序与视觉顺序一致；
- 不在 SizeChanged 中形成布局递归；
- 长列表虚拟化；
- 动画只操作 Composition 友好属性；
- 减少动态效果时替换动画，不只是加快动画。

### 5.4 数据库

- 所有 schema 变化使用 migration；
- migration 不可依赖当前 UI；
- migration 前备份；
- 使用事务；
- 存储层测试使用临时目录；
- 删除支持恢复或 tombstone；
- 不把 OAuth token 或剪贴板正文写入数据库。

### 5.5 IPC

- 先验证 frame 长度，再分配；
- JSON 反序列化使用严格选项；
- 枚举未知值拒绝或映射 Unknown；
- 请求有 timeout；
- 连接断开取消相关工作；
- event 有序列号；
- UI 忽略旧 sequence；
- 未授权命令返回稳定错误，不抛出进程级异常。

## 6. 测试策略

### 6.1 实施模型可自动完成

- 纯布局算法单元测试；
- IPC frame/JSON/版本测试；
- migration 测试；
- Provider 调度测试；
- 命令权限测试；
- ViewModel 测试；
- 故障注入；
- 静态分析；
- 构建和打包检查。

### 6.2 必须真实 Windows 会话验证

- 入口实际位置；
- 鼠标穿透；
- 自动隐藏热区；
- Explorer 重启；
- 多屏和 DPI；
- 面板窗口层级；
- 点击外部关闭；
- 动画反向；
- 拖动抓取偏移；
- 输入法；
- Narrator；
- WPR/WPA；
- 安装和升级。

若当前运行环境不能访问真实 GPU、Explorer 或交互桌面，必须写：

```text
自动测试通过；真实桌面交互验证未运行，不能宣称该验收项完成。
```

## 7. 性能验证纪律

性能任务必须记录：

- 机器 CPU、内存、GPU；
- Windows 构建；
- 显示器和刷新率；
- DPI；
- Release/Debug；
- 是否连接调试器；
- 启用卡片；
- 采样间隔；
- 测量时长；
- 工具和命令。

不要通过任务管理器瞬时截图得出最终结论。使用 WPR/WPA 或等价 ETW。

最低场景：

1. Launcher＋Broker 空闲 10 分钟；
2. 面板打开静止 5 分钟；
3. 滚动 60 秒；
4. 拖动 30 次；
5. 系统监控可见 5 分钟；
6. 面板关闭后检查 VSync 等待；
7. 24 小时内存趋势。

## 8. Git 工作方式

### 8.1 开始

```powershell
git status --short
git branch --show-current
git log -5 --oneline
```

### 8.2 提交前

```powershell
git diff --check
git status --short
```

再运行任务规定的构建和测试。

### 8.3 提交

推荐：

```text
feat(launcher): resolve taskbar edge from monitor work area
test(layout): reject overlapping card placements
fix(panel): keep modal scope open during file picker
perf(cards): pause hidden chart rendering
docs(status): record launcher POC evidence
```

不要使用：

```text
update
fix stuff
WIP
final
```

## 9. 文档同步

每个工作包结束检查：

- 需求是否改变；
- API 是否改变；
- ADR 是否需要；
- 测试矩阵是否改变；
- 实施状态是否更新；
- 待决事项是否新增；
- 开源依赖是否新增。

若实现证明原假设错误，先更新 ADR 和需求，再继续扩大实现。

## 10. 停止条件

出现以下情况必须停止并请求方向：

- 只有私有任务栏 API 才能继续；
- 需要管理员权限才能完成基础功能；
- 插件权限无法技术执行却准备向用户宣称已隔离；
- 需要复制不兼容许可证代码；
- 数据迁移有丢失风险且没有备份；
- 用户工作树包含重叠的未提交修改；
- 性能预算明显不可能达到，需要改变进程架构；
- 真实验证与自动测试结论冲突。

## 11. Luna 首个建议任务

在开始代码前，建议给 Luna 以下任务：

```text
目标：
为 WinWidgetBoard 建立最小、可构建的 M1.0 解决方案，不实现产品功能。

要求：
1. 阅读 AGENTS.md、README、docs/01、03、05、08 和 ADR。
2. 锁定 .NET 10、Windows App SDK、Windows SDK 和 MSVC 版本。
3. 创建 WinWidgetBoard.sln、Contracts、LauncherHost、WorkspacePanel 和 UnitTests。
4. LauncherHost 只创建普通隐藏消息窗口；WorkspacePanel 只显示普通测试窗口。
5. 不实现任务栏覆盖、不添加第三方插件、不引入 SQLite。
6. 增加一条 smoke test 和完整构建说明。
7. 更新 implementation-status.md。
8. 使用一个 build(scope) 提交。

验证：
- Debug/Release x64 build；
- unit tests；
- git diff --check；
- git status clean。
```

完成 M1.0 后，再单独执行 LauncherHost POC。不要把两项合并。

## 12. 工作包输出模板

```markdown
## 结果

一句话说明达成了什么。

## 需求

- LCH-001
- SYS-003

## 修改

- 文件：作用

## 验证

- `command`：通过/失败和摘要
- 人工步骤：通过/未运行

## 边界

- 未实现什么
- 为什么

## 风险

- 性能：
- 安全：
- 隐私：

## Git

- 分支：
- 提交：
- 工作树：
```
