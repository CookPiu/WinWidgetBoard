# M1.0/M2.1.1 依赖与许可证记录

状态：版本已锁定并完成模块化 restore；发布依赖审查与干净环境验证待完成
工程需求：BLD-001、DAT-001、NFR-REL-002、NFR-REL-003
记录日期：2026-08-08

## 1. 范围

本文件记录 M1.0/M2.1.1 的直接包依赖、构建工具依赖、用途、来源、许可证和分发影响。M2.1.1 首次引入 SQLite 存储基础；不引入 HTTP 客户端扩展、插件运行时、第三方 UI 框架、图标、字体或参考项目源码。

所有 NuGet 版本必须由 `Directory.Packages.props` 集中管理，不允许在项目文件中写浮动版本。`.NET SDK 10.0.302` 由 `global.json` 锁定；Windows SDK API、MSVC PlatformToolset 和 C++ 标准由中央构建属性或项目属性锁定。

## 2. NuGet 直接依赖

| 包 | 版本 | 用途 | 官方来源 | 许可证 | 产品分发影响 |
| --- | --- | --- | --- | --- | --- |
| `Microsoft.WindowsAppSDK.Runtime` | `2.3.1` | WorkspacePanel 的 Windows App SDK 运行时组件；M1.0 只对 Windows App SDK 使用自包含部署，避免修改系统运行时 | [NuGet 包页](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.3.1) | [Microsoft Windows App SDK 软件许可条款](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.3.1/License) | M1.0 会复制 Windows App SDK 运行时文件到测试输出；.NET 仍为 framework-dependent，正式产品采用何种 .NET/Windows App SDK/MSIX 分发方式由后续打包决策复核 |
| `Microsoft.WindowsAppSDK.WinUI` | `2.3.2` | WorkspacePanel 的 WinUI 3 组件，仅引入 UI 所需模块，不引入未使用的 AI、ML、Widgets 和 DWrite 元包组件 | [NuGet 包页](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.2) | [Microsoft Windows App SDK 软件许可条款](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.2/License) | 随 Windows App SDK 自包含测试输出进入；正式发布前复核传递的 WebView2、Base、Foundation 和 InteractiveExperiences 组件 |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.26100.8249` | 固定 Windows SDK 构建工具和资产，减少对开发机最高 SDK 的隐式依赖 | [NuGet 包页](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.26100.8249) | [Microsoft Windows SDK 软件许可条款](https://aka.ms/WinSDKLicenseURL) | 预期仅用于构建，不作为应用运行时功能；发布前仍需检查实际包输出和许可要求 |
| Windows 系统 `winsqlite3.dll` | 当前受支持 Windows 11 系统组件；版本不由本仓库浮动打包 | CoreBroker 的 SQLite 连接、事务、WAL 和迁移备份基础；不进入 LauncherHost | 随 Windows 系统组件条款处理；不复制第三方源码或 DLL | 应用不重新分发该 DLL；首发 Windows 11 x64 门禁必须确认系统组件存在，缺失时应安全报告不可用 |
| `Microsoft.NET.Test.Sdk` | `18.8.1` | 测试发现、测试宿主及 `dotnet test`/VSTest 集成 | [NuGet 包页](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.8.1) | [MIT](https://licenses.nuget.org/MIT) | 仅开发和测试使用，不随产品发布 |
| `MSTest` | `4.3.3` | MSTest 官方元包，提供测试框架与适配器依赖 | [NuGet 包页](https://www.nuget.org/packages/MSTest/4.3.3) | [MIT](https://licenses.nuget.org/MIT) | 仅开发和测试使用，不随产品发布 |

Microsoft 官方 NuGet 页面将 Windows App SDK 组件和 Windows SDK BuildTools 指向各自的 Microsoft 软件许可条款。它们不是 MIT 包，不得按开源 MIT 依赖记录。这些 Microsoft 包及其传递依赖、可分发文件和 Notice 要求必须在正式打包前再次复核。

M1.0 不引用完整 `Microsoft.WindowsAppSDK` 元包。官方 Windows App SDK 2.x 支持按组件引用；当前普通测试窗口只需要 Runtime 与 WinUI。这样可避免把未使用的 AI、ML、Widgets 和 DWrite 组件带入依赖图，也避免稳定 UI 基线间接解析实验性 ML 传递包。M2.1.1 的 SQLite 适配只进入 CoreBroker 和测试路径，不进入 LauncherHost。当前实现使用 Windows 11 系统 `winsqlite3.dll` 的最小 C API 适配，避免新增 NuGet 下载和原生打包负担；这不是对所有 Windows 版本均存在该 DLL 的承诺。

当前 locked-mode restore 已解析 Runtime、WinUI、Windows SDK BuildTools、Microsoft.NET.Test.Sdk 与 MSTest 的锁定依赖图，且锁文件中不包含 AI、ML、Widgets、DWrite 或实验性版本。这只证明本地模块化 restore；不代表干净 CI、正式发布输出、Notice、SBOM 或全部传递依赖许可证审查已经完成。

`Microsoft.NET.Test.Sdk` 和 `MSTest` 为 MIT 许可，但仍应在依赖清单中保留版本和来源。它们只允许进入测试项目或开发工具路径，不应被产品项目引用或复制到正式产品输出。

## 3. 工具链依赖

| 工具 | 锁定值 | 用途 | 来源与许可处理 | 分发影响 |
| --- | --- | --- | --- | --- |
| .NET SDK | `10.0.302` | restore、编译和测试 .NET 10 项目 | Microsoft 官方 .NET SDK；安装和使用受 Microsoft 对应版本条款约束 | 构建工具；应用运行时分发方式在打包阶段单独复核 |
| Visual Studio Community 2026 | `18.8.2` | IDE、MSBuild、C++ 工具集管理 | Microsoft 官方安装；遵守 Visual Studio Community 许可及适用资格 | 不随产品分发 |
| MSBuild | `18.8` | 统一构建 `.sln`、C++ 和 C# 项目 | 随 Visual Studio 安装 | 不随产品分发 |
| MSVC v143 | `14.44.35207` | LauncherHost C++23 编译 | 随 Visual Studio C++ 工作负载安装 | 已按实际链接方式复核：LauncherHost 为 `/MD`，导入 `VCRUNTIME140.dll`、`VCRUNTIME140_1.dll`、`MSVCP140.dll`，三者随发布包以 application-local 方式分发（Visual Studio 可分发代码条款允许），不安装任何机器级运行时；UCRT（`api-ms-win-crt-*`）属 Windows 组件，不分发 |
| Windows SDK API | `10.0.26100.0` | Windows 头文件、库和 API 目标 | Microsoft Windows SDK | 仅按 Windows SDK 条款分发允许的 redistributable 文件 |

本机还安装了 MSVC v145 `14.51.36231` 和 Windows SDK `10.0.28000.0`，但它们不是 M1.0 项目基线。构建不得因为本机存在更新工具而静默切换版本。

## 4. 传递依赖与发布门禁

NuGet restore 会带入传递依赖。版本锁定完成后必须生成并审查传递依赖清单，至少检查：

1. 包名、解析版本和来源；
2. 许可证及 Notice；
3. 是否进入产品输出；
4. 是否包含原生 DLL、运行时包、分析器或仅构建资产；
5. 是否存在 GPL、AGPL、Anti-996 或额外限制；
6. 是否与项目最终许可证及分发方式兼容。

在正式发布前必须：

- 对锁定文件执行干净 restore；
- 保存直接和传递依赖清单；
- 检查 WorkspacePanel 发布目录中的 Windows App SDK 文件；
- 检查 LauncherHost 的动态依赖，确认未引入 WinUI、SQLite、HTTP 或插件运行时；
- 生成第三方 Notice 和 SBOM；
- 重新打开上述 Microsoft 官方许可链接，确认条款和可分发文件范围未变化；
- 确认测试包没有进入正式产品输出。

**当前状态（2026-09-12，为 `v0.1.0` 预发布执行）**：上述清单中的 Notice 与 SBOM 已完成。
面向使用者的第三方声明是仓库根目录的 [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)，
随发布包一同分发；机器可读的 SBOM 由 [scripts/New-ThirdPartySbom.ps1](../../scripts/New-ThirdPartySbom.ps1)
在发布流水线中**针对已装配好的产物**生成（CycloneDX 1.5，`sbom.cdx.json`），因此它描述的是实际打包的
文件而不是仓库引用，且不会与构建脱节。产物仍不做代码签名。

## 5. 变更规则

新增或升级依赖时，必须在同一工作包中更新本文件，并记录：

- 引入原因和替代方案；
- 精确版本，不使用 `*`、版本范围或隐式浮动；
- 许可证；
- 包大小和运行时影响；
- 是否进入 LauncherHost 常驻路径；
- 是否进入产品分发；
- restore、build、test 和必要的许可证复核结果。

未经单独批准，不得复制第三方源码、示例资源、图标、字体或动画。GPL、AGPL、Anti-996 及带额外限制的代码不得进入当前代码库。
