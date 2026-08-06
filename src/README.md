# 源码目录规划

当前仅建立规划，不应在架构基线确认前生成大规模样板代码。

预计结构：

```text
src/
├─ LauncherHost/       # C++/Win32 任务栏入口
├─ WorkspacePanel/     # C# WinUI 3 半屏工作台与设置
├─ CoreBroker/         # 后台调度、持久化、通知和数据提供器
├─ PluginHost/         # 第三方插件进程宿主
├─ Contracts/          # IPC 与插件契约
├─ Cards.BuiltIn/      # 内置卡片
└─ Packaging/          # MSIX、安装与更新
```

创建解决方案时必须先完成 `docs/05-implementation-plan.md` 的 M1 前置检查。
