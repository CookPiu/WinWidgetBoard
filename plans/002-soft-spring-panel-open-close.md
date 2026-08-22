# 002：WorkspacePanel 锚定式软弹簧开合

- 状态：待执行
- 优先级：高
- 审查基线：`4531b4a`
- 审查日期：2026-08-22
- 范围：只调整 `WorkspacePanel` 顶层面板的打开、关闭和中途反向。

## 设计结论

正常模式不应以透明度为主角。面板应从 LauncherHost 入口对应的角落以清楚的空间幅度展开，在完整尺寸处有一次极轻的自然收束；关闭时沿相同锚点撤回。这样得到的是有方向感的“软弹簧”动效，而不是延长的淡入淡出。

“减少动态效果”仍保留，但只是无障碍替代路径：比例恒为 1、位移恒为 0，仅执行短淡入淡出。它不参与正常模式的视觉评判。

## 已确认的基础

- `PanelMotionController` 已有状态和速度，`RequestOpen/RequestClose` 可从实时呈现反向（`src/WorkspacePanel/Motion/PanelMotionController.cs:19-145`）。
- `PanelMotionCoordinator` 使用一个仅在过渡期间运行的 16 ms 调度器，且关闭稳定后才隐藏驻留窗口（`src/WorkspacePanel/Motion/PanelMotionCoordinator.cs:63-82,132-179`）。
- `MainWindow.ApplyPanelMotion` 已把偏移作用在原生顶层窗口，并以 LauncherHost 推导的 `RenderTransformOrigin` 缩放（`src/WorkspacePanel/MainWindow.xaml.cs:1811-1861`）。不要改成仅平移 XAML 内容。
- 当前正常关闭姿态仅为 `Scale=0.99` 与锚定方向 `±10` DIP（`PanelMotionController.cs:40-42`、`MainWindow.xaml.cs:209-222`）；所有通道同步，故观感近似单调淡入淡出。

## 目标动效合同

| 项目 | 目标 |
| --- | --- |
| 正常起始姿态 | `Opacity=0`、`Scale=0.965`、沿锚点方向 `±28` DIP。 |
| 不透明度 | 独立、无超调的临界阻尼，角频率 `20.5 rad/s`；尽快达到可读性，不遮盖几何运动。 |
| 缩放与位移 | 解析欠阻尼弹簧：自然角频率 `18.0 rad/s`、阻尼比 `0.68`。 |
| 软着陆 | 从静止完整开合时，约 176 ms 首次通过终点，约 238 ms 出现一次收束；越界最多为行程的约 5.4%。 |
| 上限 | 28 DIP 位移最多越界约 `1.52` DIP；打开 `Scale≤1.0019`，关闭 `Scale≥0.9631`。 |
| 稳定态 | `Opacity=1, Scale=1, Offset=(0,0)`，不改变布局或输入。 |
| 快速反向 | 保留实时值和速度；不可跳回起点、不可重播完整软着陆。 |
| 减少动态效果 | 维持现状：无缩放、无位移、短淡入淡出。 |

这不是重复弹跳：每次完整的静止起步开合只允许一次受限的几何收束。快速反向时不得硬裁剪速度；以连续折返而非强行保持理论上限为准。

## 实施步骤

1. 在 `src/WorkspacePanel/Motion/PanelMotionController.cs`：
   - 将正常关闭姿态改为 `new(0, 0.965, closedOffsetX, closedOffsetY)`；
   - 让不透明度继续使用无超调临界阻尼，但使用独立角频率 `20.5`；
   - 保留减少动态效果、状态机、实时速度和收敛语义。

2. 在 `src/WorkspacePanel/Motion/` 新增最小的解析求解器（建议 `DampedSpring.cs`），只支持本方案需要的 `0 < dampingRatio < 1`。输入为当前位置、速度、目标、自然角频率、阻尼比、elapsed；输出为下一值和速度。禁止逐帧线性插值、固定关键帧、额外计时器或通用动效框架。

3. 用该求解器推进 `Scale`、`OffsetX`、`OffsetY`，参数固定为 `18.0/0.68`；不透明度不使用欠阻尼。仍由现有 `PanelMotionCoordinator` 的一个 16 ms 计时器调度。

4. 在 `src/WorkspacePanel/MainWindow.xaml.cs` 中，把两个关闭偏移由 `-10/10` 改为同方向 `-28/28`。不得修改 `CalculateTransformOrigin`、`ApplyPanelMotion`、`ToPhysicalPixels` 和 `NativeWindowStyles.Move`。

5. 在 `tests/UnitTests/PanelMotionControllerTests.cs`：
   - 将初始断言改为 `Scale=0.965`、偏移 `-28/28`；
   - 以“受限软着陆”替换“全程无超调”：静止完整开合最多一次反向，位移越界不超过 `1.52` DIP，比例符合上表；
   - 保留并加强反向首帧连续性、减少动态效果无位移/缩放、30/120 FPS 一致性和稳定关闭精确归位；
   - 为新求解器增加静止开合及携带速度重定向的单测。

6. 在 `docs/status/implementation-status.md` 记录一条当前事实：面板正常模式使用 `28 DIP / 0.965` 锚定空间启动和受限软弹簧收束；减少动态效果与实时反向保留。不要新增 ADR。

## 边界

- 不修改 LauncherHost、任务栏入口指示器、PanelGeometry、IPC、SQLite、卡片拖拽或 SurfaceMotionCoordinator。
- 不改变模态关闭门禁、驻留隐藏和 `AppWindow.Changed` 重开路径。
- 不添加旋转、模糊、阴影脉冲、背板闪烁、布局属性动画、全屏遮罩或内容分段入场。
- 可见主运动应在约 300–350 ms 内完成；不允许多次弹跳或长尾。
- 不透明度始终限制在 `[0,1]`，但不可用硬裁剪破坏几何反向连续性。

## 验证

1. 相关单测：

   ```powershell
   dotnet test tests/UnitTests/WinWidgetBoard.UnitTests.csproj --filter "FullyQualifiedName~PanelMotionControllerTests" --no-restore
   ```

2. 恢复后构建受影响的 Release x64 面板：

   ```powershell
   dotnet build src/WorkspacePanel/WinWidgetBoard.WorkspacePanel.csproj -c Release -p:Platform=x64 --no-restore
   ```

3. 运行 `git diff --check`，不提交构建输出、日志、数据库或视频。

4. L2 真实桌面验收：固定 Windows 11、中文界面、主题、显示器、DPI、任务栏、LauncherHost 和数据状态（建议沿用 3200×2000、200% DPI、底部任务栏）。使用隔离数据启动 Release 进程：
   - 打开时应先看到从入口角展开的空间运动，再感到一次微小收束；关闭时沿同一锚点撤回；
   - 连续“打开 → 80–150 ms → 关闭 → 再打开”至少 10 次，无闪白、跳位、失焦、提前隐藏、残留呈现或重播完整软着陆；
   - 通过 `scripts/WinWidgetBoard.UiAutomation.psm1` 的 `Save-WindowCapture` 保存同条件的开前、打开后、关闭后截图；视频只放系统临时目录；
   - 打开 Windows“减少动态效果”后，确认只淡入淡出且位置、比例不变；
   - 通过 `tests/UiAutomation/Test-LauncherEntryPlacement.ps1` 或等价隔离 UIA 流程复核入口打开、关闭后重开和驻留语义。

通过标准：正常模式的主感知是锚定空间运动与一次柔和收束，而不是透明度变化；没有卡通感、拖沓或额外等待；反向稳定且无障碍/驻留行为不回归。
