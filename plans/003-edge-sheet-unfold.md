# 003：Launcher 边缘纸张展开

- 状态：已完成（代码提交：`4a9dfe4`；L2：`REAL-EDGE-SHEET-PASS`）
- 优先级：高
- 审查基线：`4531b4a`
- 范围：只改变 `WorkspacePanel` 顶层面板开合；不改变卡片、数据、LauncherHost 或常驻语义。

## 体验概念

把面板看作从 LauncherHost 所在边缘“展开”的一张轻薄面板，而不是从角落整体弹出。正常模式打开时，入口侧保持固定，面板沿入口的主轴展开；关闭时沿同一边缘收拢。它没有回弹，靠非均匀缩放和强缓出节奏获得优雅、安静但明显的方向感。

这是三套方案中最像 Windows 原生面板/纸张展开的方案，视觉冲击主要来自轴向展开，而不是弹簧。

## 目标合同

| 项目 | 参数 |
| --- | --- |
| 主轴判定 | 从 `PanelLaunchContext.LauncherRect` 与 `PanelPlacement.WindowRect` 判断 Launcher 位于面板的上、下、左或右；选择该方向为主轴。 |
| 起始比例 | 主轴 `0.90`，次轴 `0.985`；锚点继续来自 `CalculateTransformOrigin`。 |
| 起始位移 | 主轴沿入口方向 `±36` DIP，次轴 `±8` DIP。 |
| 不透明度 | `0 → 1`，独立临界阻尼，角频率 `22.0 rad/s`；只帮助显现，不主导观感。 |
| 主轴缩放与位移 | 临界阻尼，角频率 `18.5 rad/s`，无超调。 |
| 次轴缩放与位移 | 临界阻尼，角频率 `24.0 rad/s`，让横向稳定得更快。 |
| 关闭与反向 | 从实时值和速度重定向；不可回到完整打开态后再收拢。 |
| 减少动态效果 | 比例恒为 `1`、偏移恒为 `0`，只保留现有淡入淡出。 |

在底部任务栏场景中，面板将以 `ScaleY=0.90`、向下 `36` DIP 的姿态从底边展开；左/右侧入口则交换 X/Y。缩放不会触发布局重排。

## 实施边界和步骤

1. 将 `PanelMotionValue` 从单一 `Scale` 扩展为 `ScaleX` 和 `ScaleY`，只影响 `src/WorkspacePanel/Motion/PanelMotionController.cs`、`PanelMotionCoordinator.cs`、`src/WorkspacePanel/MainWindow.xaml.cs` 与对应单测。

2. 在 `MainWindow` 中新增一个私有的、基于已存在 `PanelLaunchContext` 和 `PanelPlacement` 的 `PanelEntryAxis` 判定。它只输出 `Horizontal` 或 `Vertical` 与正负方向；不要引入通用几何框架，也不要修改 `PanelGeometry`。

3. `PanelMotionController` 为 X/Y 缩放和 X/Y 偏移分别保存值与速度。正常模式按主轴/次轴使用上表临界阻尼参数；减少动态效果始终初始化为 `Opacity=0, ScaleX=1, ScaleY=1, Offset=(0,0)`。

4. `MainWindow.ApplyPanelMotion` 继续使用 `NativeWindowStyles.Move` 移动顶层窗口，并把 `ScaleX/ScaleY` 分别写到 `PanelMotionTransform`。不得改变 `CalculateTransformOrigin`、关闭门禁、驻留隐藏或 IPC。

5. 单测覆盖：
   - 底部和侧边入口各一组初始值；
   - 主轴展开单调、无超调，次轴更快稳定；
   - 打开/关闭中途反向连续；
   - 100% 与 200% DPI 下逻辑偏移到物理像素的方向正确；
   - 减少动态效果无缩放/位移，30/120 FPS 表现一致。

6. 更新 `docs/status/implementation-status.md` 一条当前事实；不新增 ADR。

## 风险与验收重点

非均匀缩放会在短时间内压缩文字与卡片比例，属于本方案刻意的视觉语汇。因此必须在 100%、150%、200% DPI 和满载卡片布局下实测，不得出现可读性模糊、圆角形变过于明显或滚动条跳动。

L2 验收固定主题、DPI、任务栏位置、语言和数据状态。分别在底部与侧边入口环境打开/关闭 10 次，并进行 80–150 ms 中途反向。通过标准：看起来像从正确边缘展开的整张面板，静而不闷；没有弹跳、失焦、闪白、跳位、布局变化或关闭驻留回归。

## 取舍

- 优点：三种中方向感最强、无弹跳、风格最克制。
- 代价：需要把当前单一缩放模型扩展为双轴缩放，回归面略大。
- 适合：希望“成熟的系统面板感”，不希望明显弹簧感。

## 实施结果

- Before：统一 `0.99` 缩放与 `±10` DIP 位移，面板在正常模式下接近淡入淡出。
- After：按 LauncherHost 最近外部边选择主轴，以 `0.90` / `0.985` 双轴缩放和 `36` / `8` DIP 偏移展开；正常模式没有弹跳，减少动态效果仍只淡化。
- Why：用入口边缘的空间关系取代单调透明度变化，同时保持专业工作台的克制节奏。
- 验证：`PanelMotionControllerTests` 7/7 通过；WorkspacePanel Release x64 构建 0 警告、0 错误；隔离真实桌面流程经鼠标关闭、驻留重开及阶段截图通过（右下入口、200% DPI，`REAL-EDGE-SHEET-PASS`）。
