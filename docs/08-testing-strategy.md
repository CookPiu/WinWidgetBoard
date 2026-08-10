# 测试与验收策略

文档状态：已批准基线
版本：0.1
日期：2026-08-06

## 1. 目标

测试必须证明：

- 任务栏入口不会破坏 Windows 基本交互；
- 面板和卡片满足真实输入契约；
- 数据在崩溃、迁移和断网情况下可靠；
- 插件与 Provider 故障被隔离；
- 性能目标有可重复测量；
- 隐私和权限不是仅存在于 UI 文案；
- 新 Windows 构建兼容性经过真实验证。

编译成功、单元测试通过或静态截图均不能单独证明任务栏集成成功。

## 2. 测试层级

```text
静态分析
   ↓
单元测试
   ↓
契约与属性测试
   ↓
进程集成测试
   ↓
UI Automation
   ↓
真实桌面交互测试
   ↓
性能、安全和长期稳定性
```

## 3. 环境矩阵

### 3.1 Windows

至少覆盖：

- 当前受支持 Windows 11 Home/Pro 构建；
- 当前受支持 Windows 11 Enterprise 构建；
- 下一个预览构建仅作兼容预警，不作为稳定承诺；
- x64；
- 干净用户配置；
- 原生 Widgets 开/关；
- Explorer 正常和重启恢复。

每次发布记录精确 build number。

### 3.2 显示

| 维度 | 值 |
| --- | --- |
| 显示器数量 | 1、2、3 |
| 缩放 | 100%、125%、150%、200% |
| 分辨率 | 1366×768、1920×1080、2560×1440、4K、超宽 |
| 刷新率 | 60、120/144 或更高 |
| 主显示器 | 左、右、中央 |
| 排列 | 水平、带负坐标、不同垂直偏移 |
| 任务栏 | 居中、左对齐、自动隐藏开/关 |

### 3.3 系统偏好

- 深色；
- 浅色；
- 高对比度；
- 文本缩放；
- 减少动态效果；
- 减少透明度；
- 节能模式；
- 专注/免打扰；
- 简体中文和英语；
- 12/24 小时制；
- 不同时区和区域格式。

### 3.4 特殊会话

- 锁定/解锁；
- 睡眠/唤醒；
- RDP 连接/断开；
- Explorer 崩溃/手动重启；
- 全屏游戏或视频；
- 无边框全屏；
- UAC；
- 多用户切换；
- 显示器热插拔。

## 4. 测试标识

测试 ID：

```text
UT-模块-编号
CT-协议-编号
IT-边界-编号
UI-场景-编号
PERF-场景-编号
SEC-场景-编号
MAN-场景-编号
```

测试名称或说明必须引用需求 ID。

示例：

```text
UT-LAYOUT-004 [LYT-005] Compaction preserves unrelated relative order
UI-LAUNCHER-007 [LCH-004] Transparent area passes clicks to taskbar
PERF-IDLE-001 [NFR-PERF-001] Default background working set
```

## 5. 静态分析

### 5.1 C++

- 编译器高警告等级；
- `/permissive-`；
- 静态分析；
- 禁止未检查的窄化；
- Win32 HANDLE 生命周期检查；
- Unicode；
- 安全编译选项；
- 依赖漏洞与许可证清单。

### 5.2 C#

- nullable；
- warnings as errors（允许有记录的例外）；
- .NET analyzers；
- async/cancellation 分析；
- XAML binding diagnostics；
- 包漏洞与许可证扫描；
- 格式化检查。

## 6. 单元测试

### 6.1 TaskbarGeometryResolver

覆盖：

- 正常底部任务栏；
- 左右/顶部工作区差异；
- 自动隐藏导致工作区无差异；
- 负坐标显示器；
- 不同 DPI；
- 过小安全区域；
- 用户偏移越界；
- Shell 修改导致矩形异常；
- fallback 选择；
- 相同输入确定性。

不得在纯单元测试中声称真实任务栏检测成功。

### 6.2 Launcher 输入状态机

覆盖：

- down/up inside；
- down/move outside/up；
- 拖动阈值；
- pointer capture lost；
- 双击不延迟单击；
- 右键；
- 快捷键；
- 面板打开过程中再次点击；
- 进程启动失败。

### 6.3 Panel 动画状态机

覆盖：

- Closed→Opening→Open；
- Opening→Closing；
- Closing→Opening；
- Open→Closing→Closed；
- 减少动态效果；
- 显示器变化中取消；
- 进程退出时清理；
- presentation value 连续。

### 6.4 Layout Engine

示例测试：

- 标准尺寸放置；
- 宽卡片跨列；
- 超出当前列数；
- 添加/删除紧凑；
- 移动；
- 占用单元格的确定性让位；
- 拖动预览不替换正在捕获指针的表面对象；
- 缩放；
- preferred column / preferred row 逻辑单元格；
- 超过紧凑布局底部 2 个空行的异常 preferred row 仅在内存中回退；
- 持久化顺序重放后模板身份、表面身份和 placement 保持一致；
- 已提交 placement 变化会触发自定义网格重新排列；
- 2/4/6 列重排；
- 相同输入相同输出；
- 无关卡片相对顺序；
- 100 卡片性能；
- undo/redo。

属性：

```text
NoOverlap
WithinBounds
SupportedSpanOnly
Deterministic
AllInstancesPlacedOnce
StableRelativeOrder
RoundTripPersistence
```

使用固定随机种子保存失败 case。

### 6.5 ProviderScheduler

- 合并相同来源请求；
- 可见/隐藏间隔；
- 取消；
- 超时；
- 指数退避；
- 网络恢复；
- 节能模式；
- 手动刷新限流；
- Provider 抛异常；
- 系统时钟变化。

### 6.6 数据

- 每个 migration；
- 重复运行 migration；
- migration 中断；
- 备份；
- revision 冲突；
- SQLite busy；
- 磁盘满；
- 只读目录；
- 损坏数据库；
- 导入未知字段；
- 敏感字段不导出。

### 6.7 卡片

每张内置卡片至少测试：

- ViewModel 状态；
- Loading/Ready/Empty/Stale/Error；
- action；
- persistence；
- cancellation；
- localization；
- accessibility summary；
- hidden lifecycle；
- invalid Provider payload。

## 7. 契约测试

基于 `docs/07-api-contracts.md`：

- frame 分片和合并；
- 长度字段边界；
- UTF-8；
- JSON 必填字段；
- protocol major/minor；
- ID 格式；
- sequence；
- revision；
- idempotency；
- permission；
- UI Schema 限制；
- 错误映射；
- 向后兼容 golden files。

Contracts 项目应为 C++ 和 C# 生成或共享相同 golden payload，防止双端解释不同。

## 8. 集成测试

### 8.1 IPC

- 启动 CoreBroker 临时实例；
- Launcher/Panel 握手；
- 断开重连；
- Broker 重启；
- 慢客户端背压；
- 未授权客户端；
- 同一用户/不同用户 ACL；
- 超大消息；
- 乱序事件；
- 取消传播。

### 8.2 数据库与 Broker

- 临时用户数据目录；
- 写入卡片、布局、便签和计时器；
- 杀死进程；
- 重启恢复；
- 通知计划；
- 导入导出；
- 凭据仅保存引用。

### 8.3 Provider

- Fake Weather Server；
- 离线；
- 429/500；
- 慢响应；
- 无效证书由系统 HTTP 栈拒绝；
- 内容超限；
- Provider crash；
- 退避；
- 缓存新鲜度。

## 9. UI Automation

### 9.1 覆盖范围

- 打开/关闭面板；
- 标题区焦点；
- 添加卡片；
- 进入编辑；
- 键盘移动和缩放；
- 新增便签；
- 新增待办；
- 启动计时器；
- 打开剪贴板条目；
- 设置；
- 错误和权限状态。

### 9.2 限制

UI Automation 可验证焦点、操作和可访问树，但无法充分证明：

- 透明点击穿透；
- 任务栏自动隐藏；
- 动效连续；
- 全屏层级；
- 真实多显示器位置；
- 帧时间。

这些必须进入真实交互测试。

## 10. 真实桌面交互

### 10.1 Launcher

- [ ] 入口位于预期安全区域。
- [ ] 不覆盖开始、搜索、任务按钮、系统托盘。
- [ ] 按钮外点击落到系统任务栏。
- [ ] 按下反馈立即出现。
- [ ] 拖出后释放不打开。
- [ ] 右键菜单不激活错误窗口。
- [ ] 自动隐藏可正常触发。
- [ ] 全屏时入口隐藏。
- [ ] Explorer 重启后恢复一次且无重复入口。

### 10.2 Panel

- [ ] 在入口显示器打开。
- [ ] 不跨屏。
- [ ] 点击外部关闭。
- [ ] `Esc` 层级正确。
- [ ] 文件选择器期间不误关。
- [ ] 再次点击可中途反向。
- [ ] 关闭后焦点语义返回入口。
- [ ] Alt+Tab 不出现多余窗口。

### 10.3 Drag

- [ ] 便签、计时器、待办和本地日历均可从非交互内容区或拖动柄开始拖动。
- [ ] 卡片内按钮和文本框不被根表面拖动抢占。
- [ ] 从卡片边缘抓取不会跳到中心。
- [ ] 未过阈值仍按点击处理。
- [ ] 过阈值后不触发点击。
- [ ] 移出窗口仍跟随。
- [ ] `Esc` 恢复。
- [ ] 其他卡片连续让位。
- [ ] 占用落点的卡片确定性进入腾出的单元，不随机互换身份。
- [ ] 释放无重叠。
- [ ] 拖到边缘有阻力而非冻结。

当前受控回归已在 Release x64 下通过计时器/日历的拖动柄与卡片表面双向换位。`-WithBroker` 路径会等待真实持久化布局完成有界恢复后再拖动，并验证测试不点击“完成”时数据库修订和异常行不被静默改写；无 Broker 路径验证默认布局仍可交互。该证据不替代四卡、DPI、多显示器和刷新率矩阵。

## 11. 性能测试

### 11.1 测量条件

记录：

- Git commit；
- Release x64；
- Windows build；
- 硬件；
- 显示器/DPI/刷新率；
- 启用卡片；
- Provider 间隔；
- 是否联网；
- 调试器状态；
- 测量时长。

### 11.2 Idle Power

管理员 PowerShell：

```powershell
wpr -start power -filemode
# 保持测试状态 5～10 分钟
wpr -stop idle-power.etl
```

在 WPA 检查：

- CPU Usage (Precise/Sampled)；
- context switches；
- thread wakeups；
- timers；
- LauncherHost/CoreBroker；
- 网络与磁盘。

### 11.3 GPU/VSync

```powershell
wpr -start gpu -filemode
# 面板关闭空闲 5 分钟
wpr -stop idle-gpu.etl
```

检查：

- WaitForVSync；
- DxgKrnl 事件；
- 隐藏 WorkspacePanel 是否仍渲染；
- Launcher 是否持续提交帧。

### 11.4 启动

分别测：

- Panel 冷启动；
- Panel 保温热启动；
- CoreBroker 已运行/未运行；
- 首帧；
- 基础可交互；
- 首张真实卡片状态。

至少 30 次，报告 median、P90、P95，不只报告最快一次。

### 11.5 帧时间

场景：

- 面板打开/关闭 30 次；
- 快速反向 30 次；
- 100 卡片滚动；
- 卡片拖动和自动让位；
- 系统监控图表可见；
- 深浅色切换。

记录长帧和掉帧，不仅平均 FPS。

### 11.6 内存

- 后台 10 分钟；
- 面板打开 10 分钟；
- 打开/关闭 100 次；
- 添加/删除卡片 100 次；
- 图片剪贴板 50 次；
- 24/72 小时 soak。

报告 working set、private bytes、GC heap、native allocations 和趋势。

## 12. 故障注入

- 杀死 WorkspacePanel；
- 杀死 CoreBroker；
- Explorer 重启；
- Provider 崩溃；
- 网络断开；
- DNS 失败；
- SQLite busy/locked；
- 磁盘满；
- 配置 JSON 截断；
- Boot Snapshot 旧版本；
- 时钟向前/向后变化；
- 时区改变；
- 剪贴板被其他进程长期占用；
- 插件发送 2MiB 消息；
- 插件每秒发送上千事件；
- OAuth token 失效。

每项必须验证用户可见状态和恢复路径。

## 13. 安全测试

详见 `docs/09-security-privacy.md`，至少包括：

- Pipe ACL；
- session token 重放；
- frame 长度攻击；
- JSON 深度；
- 路径遍历；
- 插件包 zip slip；
- UI Schema 节点炸弹；
- 命令越权；
- 网络域名绕过；
- 凭据日志泄露；
- 剪贴板诊断泄露；
- 更新包签名；
- fullTrust 插件警告。

## 14. 发布门禁

### 14.1 严重等级

- P0：数据丢失、任意代码执行、系统任务栏不可用、无法卸载；
- P1：核心流程不可用、隐私泄露、持续高资源、频繁崩溃；
- P2：重要功能缺陷、有明确绕过；
- P3：轻微视觉、文案或边缘问题。

稳定发布要求：

- P0 = 0；
- P1 = 0；
- P2 有负责人、绕过和计划；
- 所有 MVP 验收场景完成；
- 性能基线有结果；
- 安全清单完成；
- 第三方 Notice 完整；
- 安装/升级/卸载验证；
- 数据备份恢复演练。

## 15. 需求追踪样例

| 需求 | 自动测试 | 人工/性能 |
| --- | --- | --- |
| LCH-004 | Launcher hit-test state unit test | UI-LAUNCHER-007 |
| PNL-004 | Panel state machine unit test | UI-PANEL-004 |
| LYT-005 | Layout property tests | UI-LAYOUT-006 |
| CLP-001 | Clipboard provider and persistence tests | SEC-CLIPBOARD-001 |
| NFR-PERF-001 | Metrics collection harness | PERF-IDLE-001 |
| NFR-A11Y-001 | UIA automation | MAN-A11Y-001 |

M2 开始时建立完整 `requirements-traceability.md`，由 CI 检查有效需求 ID。

## 16. 测试证据目录

不把大型二进制证据提交 Git。建议本地：

```text
artifacts/
├─ test-results/
├─ screenshots/
├─ recordings/
├─ perf/
│  ├─ etl/
│  └─ reports/
└─ diagnostics/
```

Git 只提交：

- 小型 Markdown 摘要；
- 测量条件；
- 指标表；
- 必要的脱敏截图；
- 自动测试报告链接。
