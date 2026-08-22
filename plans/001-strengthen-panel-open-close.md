# 001：强化 WorkspacePanel 锚定式开合反馈

- 状态：待执行
- 优先级：中
- 审查基线：`4531b4a`
- 审查日期：2026-08-22
- 范围：仅 `WorkspacePanel` 顶层面板的打开、关闭与反向切换；不触及卡片、弹层或任务栏入口的独立动效。

## 问题与结论

当前开合链路在工程质量上是可靠的：它以当前呈现值和速度为起点反向，使用单个仅在过渡期间运行的 16 ms 计时器，并在“减少动态效果”时移除缩放与位移。这些行为应保留。

但正常模式的关闭姿态只有 `0.99` 缩放和沿锚点方向 `10` 个逻辑像素的原生窗口位移。对于规范中的 680–960 DIP 宽、90% 工作区高的面板，这一幅度容易被不透明度变化吞没；在 200% DPI 参考环境中，位移仅为约 20 个物理像素。因此打开和关闭接近单调的淡入淡出，未充分说明它与 LauncherHost 入口的空间关系。

结论：保持现有临界阻尼弹簧、锚点计算、原生窗口移动和无弹跳原则，只将正常模式的起止空间幅度增强为“可感知但克制”的 `0.97` 缩放与 `24` DIP 锚定方向位移。这样可让面板从入口方向生长、向入口方向退场，而不增加等待或视觉噪声。

## 已核对的现有实现

```csharp
// src/WorkspacePanel/Motion/PanelMotionController.cs:19-42
private const double SpringAngularFrequency = 15.491933384829668;
...
_closedValue = reducedMotion
    ? new(0, 1, 0, 0)
    : new(0, 0.99, closedOffsetX, closedOffsetY);
```

```csharp
// src/WorkspacePanel/MainWindow.xaml.cs:209-229
RootGrid.RenderTransformOrigin = transformOrigin;
_motion = new PanelMotionCoordinator(
    transformOrigin.X < 0.5 ? -10 : 10,
    transformOrigin.Y < 0.5 ? -10 : 10,
    _reducedMotion,
    ...);
```

`MainWindow.ApplyPanelMotion`（`src/WorkspacePanel/MainWindow.xaml.cs:1823-1861`）已经将偏移施加到顶层原生窗口，并将缩放固定在由 LauncherHost 推导出的 `RenderTransformOrigin` 上；不要用 XAML 内容平移替换它。`PanelMotionCoordinator`（`src/WorkspacePanel/Motion/PanelMotionCoordinator.cs:63-82,132-179`）会保留当前值和速度、在关闭稳定后才隐藏驻留窗口，亦不应改变。

现有项目对辅助渐进披露表面使用 `180 ms` 进入、`125 ms` 退出、`150 ms` 减少动态效果和无弹跳的缩放约定（`src/WorkspacePanel/Motion/SurfaceMotionCoordinator.cs:22-31,79-99,125-135`）。顶层面板已有与 LauncherHost 指示器同步的临界阻尼模型，因此本方案不把该面板改为另一套固定时长或缓动曲线。

## 目标动效合同

| 项目 | 当前 | 目标 | 原因 |
| --- | --- | --- | --- |
| 正常模式关闭缩放 | `0.99` | `0.97` | 让大面板的缩放可感知，仍处于克制的 3% 范围内。 |
| 正常模式关闭位移 | 沿锚点方向 `±10` DIP | 同一方向 `±24` DIP | 在 200% DPI 下约为 48 物理像素，清楚表达入口方向且不产生飞入感。 |
| 打开姿态 | `Opacity=1, Scale=1, Offset=(0,0)` | 不变 | 到达稳定状态不改变布局或尺寸。 |
| 曲线 | `15.491933384829668` 临界阻尼弹簧 | 不变 | 保持任务栏入口与面板的共同节奏、帧率稳定和无超调。 |
| 减少动态效果 | `Opacity` 渐变；缩放和位移为零 | 不变 | 保持无空间运动的可访问性合同。 |
| 关闭时机 | 动画稳定后隐藏驻留窗口 | 不变 | 保持 ADR-0025 的热驻留与模态关闭策略。 |

打开与关闭必须继续从实时的呈现值反向；重复点击入口或按关闭键时，不得先跳回“完全打开/完全关闭”再开始下一段动效。

## 实施步骤

1. 在 `src/WorkspacePanel/Motion/PanelMotionController.cs` 中，仅把非减少动态效果分支的关闭缩放由 `0.99` 改为 `0.97`。保留 `SpringAngularFrequency`、`ReducedMotionTimeConstant`、收敛阈值、状态机和逐轴临界阻尼计算。

2. 在 `src/WorkspacePanel/MainWindow.xaml.cs` 中，将传给 `PanelMotionCoordinator` 的两个关闭偏移由根据 `transformOrigin` 选择的 `-10/10` 改为同方向的 `-24/24`。不要改变 `CalculateTransformOrigin`、`ApplyPanelMotion`、`ToPhysicalPixels` 或 `NativeWindowStyles.Move`。

3. 在 `tests/UnitTests/PanelMotionControllerTests.cs` 更新当前关闭尺度断言为 `0.97`，并将面板测试构造参数改为代表性 `-24/24` 偏移。保留以下覆盖：到达锚定打开态、从实时呈现值反向、减少动态效果无位移/缩放、单调无超调，以及 30/120 FPS 时间一致性。

4. 为避免回归，在同一测试类增加或强化两个断言：
   - 打开第一帧和反向关闭第一帧均处于前一呈现值与目标值之间，不能发生跳变；
   - 正常模式稳定关闭后精确为 `Opacity=0`、`Scale=0.97`、构造时传入的 `OffsetX/OffsetY`。

5. 在 `docs/status/implementation-status.md` 中用一条当前事实说明面板开合已采用 24 DIP/0.97 的锚定临界阻尼反馈，保留减少动态效果与实时反向；不要创建新的 ADR，也不要重复需求文档内容。

## 明确边界

- 不新增动效框架、计时器、后台进程、配置项或遥测。
- 不引入弹跳、弹性超调、旋转、模糊、阴影脉冲或布局属性动画。
- 不修改 `LauncherHost`、入口指示器的弹簧频率、`PanelGeometry`、IPC、SQLite、卡片拖拽或 `SurfaceMotionCoordinator`。
- 不改变关闭语义：模态范围内仍延迟关闭，动画收敛后仍调用 `HideForResidency`，而不是关闭进程。
- 不改变高对比度和“减少动态效果”的路径；后者只能淡入淡出，不能有位移或缩放。

## 机械验证

1. 在仓库根目录使用项目锁定工具链，运行：

   ```powershell
   dotnet test tests/UnitTests/WinWidgetBoard.UnitTests.csproj --filter "FullyQualifiedName~PanelMotionControllerTests" --no-restore
   ```

2. 构建受影响的 x64 Release 面板项目（先按仓库工具链文档完成恢复；若资产不存在，不得把缓存结果当作成功）：

   ```powershell
   dotnet build src/WorkspacePanel/WinWidgetBoard.WorkspacePanel.csproj -c Release -p:Platform=x64 --no-restore
   ```

3. 运行：

   ```powershell
   git diff --check
   ```

4. 记录测试、构建和差异检查的退出码；不要把构建输出、日志、数据库或屏幕录制文件提交到仓库。

## 真实桌面验收（L2）

固定参考环境后执行，记录 Before / After / Why：Windows 11、中文界面、同一主题、同一显示器与 DPI、同一任务栏位置、同一 LauncherHost 入口位置、相同的默认天气/便签数据。建议沿用当前 3200×2000、200% DPI、底部任务栏环境；若改用其他环境，须完整记录。

1. 使用隔离验收数据启动 Release `LauncherHost` 与 `WorkspacePanel`，不得操作用户日常驻留进程或生产数据库。
2. 通过入口打开面板，观察面板从入口对应的角向完整尺寸生长；通过标题栏关闭按钮关闭，观察其朝同一锚点退场。用 `scripts/WinWidgetBoard.UiAutomation.psm1` 的 `Save-WindowCapture` 保存开前、打开后、关闭后可比较的静态截图。
3. 连续执行“打开 → 进行约 80–150 ms → 关闭 → 再打开”至少 10 次；每次必须从当前帧平滑反向，不得闪白、跳位、失焦、提前隐藏或残留缩放/透明度。
4. 打开 Windows 的“减少动态效果”后重复一次开关：应只出现短暂淡入淡出，窗口位置和比例始终不变。
5. 通过 `tests/UiAutomation/Test-LauncherEntryPlacement.ps1` 或其等价的隔离 UIA 流程复核入口可打开面板、关闭后可再次打开；若为该流程新增开合检查，只断言可观察状态与进程驻留，不依赖固定帧时序。

验收通过标准：空间关系比当前基线更清楚，但没有弹跳、拖沓或额外等待；反向稳定、减少动态效果合规，且驻留重开与原有启动路径不回归。
