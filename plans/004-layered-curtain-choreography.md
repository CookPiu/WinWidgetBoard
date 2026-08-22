# 004：分层幕布编排开合

- 状态：待选
- 优先级：高
- 审查基线：`4531b4a`
- 范围：只为顶层面板的外壳、标题栏和内容区建立一次性开合编排；不逐卡片播放入场动画。

## 体验概念

把一次打开拆成清晰但很短的三层叙事：面板外壳先从入口抵达，标题栏紧随出现，卡片内容最后轻轻落位。用户不会等待内容加载；三个层次在约 240 ms 内完成，只是用微小的时间差建立层级和质感。关闭时反向并行，保证迅速。

这套方案不是调整一条根动效，而是“外壳 + chrome + 内容”的编排，因此是三种中最有仪式感、也最接近精致生产力工具的方案。

## 目标合同

| 层 | 打开参数 | 关闭参数 |
| --- | --- | --- |
| RootGrid 外壳 | `Scale=0.985`、沿锚点 `±16` DIP；临界阻尼 `19.0 rad/s` | 从实时值回到关闭姿态，维持驻留隐藏时机。 |
| HeaderBar | 延后 `24 ms`；透明度 `0→1`、沿入口方向 `8` DIP → `0`、`Scale=0.99→1`；`180 ms` 强缓出 `(0.23,1,0.32,1)` | 无延后，`125 ms` 同方向撤回。 |
| ContentScrollViewer | 延后 `56 ms`；透明度 `0→1`、沿入口方向 `16` DIP → `0`、`Scale=0.98→1`；`200 ms` 同一强缓出 | 无延后，`125 ms` 同方向撤回。 |
| 减少动态效果 | Root 仅淡入淡出，HeaderBar 和 ContentScrollViewer 不再独立偏移/缩放/延后。 | 同左。 |
| 反向 | 停止当前组合动画，读取现有 visual 属性并立即以新版本号重定向。 | 不得先复位或闪出默认值。 |

三个层都只使用 `Opacity`、`Scale` 和 `Translation`；不动画布局属性、不逐卡片动、不改变焦点与可用状态。

## 实施边界和步骤

1. 保留现有 `PanelMotionController` 和 `PanelMotionCoordinator` 的根面板运动结构，仅将 RootGrid 的起始幅度更新为本方案数值；不要把三层编排混入卡片回弹逻辑。

2. 新增专用的 `PanelLayerRevealCoordinator`（仅服务于本窗口，不抽象为全局框架）。它绑定 `HeaderBar` 和 `ContentScrollViewer`，使用 `ElementCompositionPreview.GetElementVisual` 建立组合动画。

3. Coordinator 必须保存递增版本号及每层的期望可见状态。打开/关闭反向时，先停止已有属性动画并以当前呈现值为起点，再开始新版本动画；旧批次的完成回调不得复位新一轮状态。

4. 在 `MainWindow.xaml.cs` 的 `RequestOpenMotion`、`RequestCloseMotion`、`ShowAfterResidency` 和关闭收尾中调用该专用 coordinator。不得修改 `HeaderBar`、`ContentScrollViewer` 的布局、绑定、滚动位置、焦点管理或卡片身份。

5. 打开前只在正常模式设置两层的组合 visual 初始值；关闭结束或新的反向稳定后必须调用 `ResetPresentation`，使 XAML 默认值恢复为 `Opacity=1, Scale=1, Translation=0`。减少动态效果和高对比度不创建分层运动。

6. 单测/集成测试覆盖版本号失效、打开后立即关闭、关闭后立即重开、Coordinator Dispose、减少动态效果、Root 隐藏前各层复位。为真实 UIA 流程增加 HeaderBar/ContentScrollViewer 仍可交互、SearchBox 可聚焦、ContentScrollViewer 滚动位置不被开合清零的检查。

7. 更新 `docs/status/implementation-status.md` 一条当前事实；不新增 ADR。

## 风险与验收重点

这是三种中状态协调最多的一种。主要风险不是性能，而是开关过快时子层动画与驻留隐藏不同步，或焦点/滚动状态被意外重置。新 coordinator 必须局限在 `WorkspacePanel`，并且不接管根面板的 native window 移动。

L2 验收固定主题、DPI、任务栏位置、语言和数据状态。打开后应先感知外壳、再感知标题与内容轻落；关闭必须并行、果断。连续 10 次 80–150 ms 中途反向，SearchBox、关闭按钮、卡片滚动和已有便签输入状态均不得受影响。减少动态效果、高对比度、驻留重开和 100%/200% DPI 也必须通过。

## 取舍

- 优点：层级最丰富、最有高级感；不依赖明显弹簧或大幅形变。
- 代价：协调状态和真实桌面验收最多。
- 适合：希望面板像一个精致的工作空间，而不是单纯系统弹层。
