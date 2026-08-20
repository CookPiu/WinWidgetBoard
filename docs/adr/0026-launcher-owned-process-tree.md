# ADR-0026：启动器持有整棵进程树，入口成为自足的安装目标

状态：Accepted
日期：2026-08-20
关系：补全 [ADR-0025](0025-resident-workspace-panel.md) 遗留的孤儿进程问题；扩展 [ADR-0007](0007-launcher-panel-process-handoff.md) 的进程接力与 [ADR-0008](0008-launcher-corebroker-client.md) 的最小原生客户端边界

## 背景

ADR-0025 让面板常驻。常驻带来一个当时未解决的缺口：启动器被**强制结束**（崩溃、任务管理器结束任务）时不会执行任何清理代码，`WorkspacePanelProcess::Shutdown` 不会被调用，常驻面板就变成孤儿，只能由用户手动结束。

同时，长期使用这条路一直没有打通。生产运行依赖 `scripts/Run-WinWidgetBoard.ps1`：它生成会话令牌、启动 CoreBroker、再前台运行 LauncherHost。这意味着入口不能做成一个普通快捷方式——只能通过一个 PowerShell 包装脚本启动，登录时还会闪出控制台窗口。该脚本还把令牌放在 CoreBroker 的**命令行**上，而命令行对同机上的其他进程是可读的。

## 决策

1. LauncherHost 创建一个带 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job Object，**所有**子进程在 `CREATE_SUSPENDED` 状态下先加入该 Job 再恢复执行。内核在进程终止时关闭句柄，因此强制结束启动器也会带走整棵树。
2. LauncherHost 自行启动 CoreBroker：用 `BCryptGenRandom` 生成会话令牌，写入**自身进程的环境变量**，令牌因此只通过环境块传递给 Broker 与面板，不出现在任何命令行或日志中。
3. 只在**没有外部提供会话**时自启动。开发脚本会自己设置令牌并启动自己的 Broker，检测到令牌已存在就完全不接管。
4. 新增 `--no-broker` 显式关闭自启动，供不允许触碰生产数据库的真实桌面测试使用（ADR-0017）。
5. 子进程路径解析顺序改为：环境变量覆盖 → 与 LauncherHost 同目录 → 同目录下的同名子目录（`WorkspacePanel\`、`CoreBroker\`）。第三条是安装布局，让三个应用各自保留依赖集，而不必平铺到同一个目录。
6. 新增 `scripts/Install-WinWidgetBoard.ps1`：复制到 `%LOCALAPPDATA%\WinWidgetBoard\app`，创建开始菜单与登录启动快捷方式，`-Uninstall` 完整回退。仅当前用户，无提权、无服务、无注册表类注册。

## 理由

- Job Object 是**唯一**能覆盖强制结束的机制：任何基于清理代码的方案在 `TerminateProcess` 面前都不会执行；
- 令牌走环境块而不是命令行，收紧了既有的暴露面，且不需要新的协议；
- 「外部已提供会话就不接管」使全部现有脚本无需改动仍然成立，`--no-broker` 则给出显式的测试隔离开关；
- 子目录探测让快捷方式可以直接指向 `.exe`：没有包装脚本，就没有登录时的控制台闪烁。

## 后果

正面：

- 强制结束启动器后不再残留任何进程（已实测）；
- 入口可作为普通程序安装、开机自启，不再依赖仓库和开发脚本；
- 会话令牌不再出现在命令行上。

负面与约束：

- LauncherHost 现在会创建 CoreBroker 进程。它仍然不链接 SQLite、HTTP 或托管 UI 运行时，边界未变，但「启动器只负责入口和面板」这句话不再准确；
- Job Object 创建失败时只降级记录日志，不阻止启动——此时回到 ADR-0025 的显式关闭路径，即失去强制结束保护；
- 安装目录是构建产物的副本，仓库重新构建后需要重新运行安装脚本；
- `Run-WinWidgetBoard.ps1` 保留原样（仍用生产数据、仍从仓库产物运行），但日常使用应改用安装后的入口。

## 放弃方案

- **在启动器退出时清理（现状）**：对强制结束无效，这正是本 ADR 要修的缺口。
- **由面板自行检测父进程消失**：需要面板轮询父进程句柄，把生命周期知识散到第二个进程里，且崩溃窗口内仍可能残留。
- **登录时运行 PowerShell 包装脚本**：会闪出控制台窗口，且把令牌留在命令行上。
- **把令牌写入注册表或文件**：比环境块更持久、更容易泄漏，收益为零。

## 验证

- `--smoke-test` 覆盖几何与入口视觉契约；
- 真实桌面：从安装目录启动，Broker 与面板分别从 `CoreBroker\`、`WorkspacePanel\` 解析成功；对启动器执行 `Process.Kill()` 后三个进程全部消失；
- `scripts/Install-WinWidgetBoard.ps1` 安装与 `-Uninstall` 往返，安装根目录与两个快捷方式全部清除。
