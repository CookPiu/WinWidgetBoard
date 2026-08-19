# M1.0 工具链基线

状态：已锁定；本地分项目构建与 smoke-test 通过，干净环境 CI 待验证
工程需求：BLD-001
记录日期：2026-08-06

## 1. 结论

M1.0 使用以下受控基线：

- .NET SDK `10.0.302`；
- Windows App SDK Runtime `2.3.1` stable 与 WinUI 组件 `2.3.2`；
- Windows SDK API 目标 `10.0.26100.0`；
- `Microsoft.Windows.SDK.BuildTools` `10.0.26100.8249`；
- MSVC `v143`，编译器工具版本 `14.44.35207`；
- Visual Studio Community 2026 `18.8.2` / MSBuild `18.8`；
- 首个技术验证配置为 Debug x64 和 Release x64。

本机系统路径当前只有 .NET Host `9.0.17`，没有安装任何 .NET SDK。为避免未授权的系统级安装，本次验证使用 Microsoft 官方便携 .NET SDK `10.0.302`；下载文件的 SHA-512 已与官方校验文件一致。当前便携 SDK 位于 `C:\tmp\winwidgetboard-dotnet-10.0.302`，并包含 .NET/WindowsDesktop Runtime `10.0.10`，已恢复托管项目 restore/build/test 和真实 CoreBroker 进程 smoke；这不等同于在干净开发机上完成系统安装，也不能替代 CI 的整套解决方案验证。

`x64` 仅是 M1 技术验证和当前 CI 兼容基线，不代表已经决定正式产品只支持 x64。ARM64 与最终首发平台范围仍需按产品决策和后续测试矩阵处理。

## 2. 本机已核实环境

| 项目 | 已核实值 | 状态与说明 |
| --- | --- | --- |
| Windows | Windows 11 Pro 25H2，OS build `26200.8737` | 已核实 |
| 注册表 `ProductName` | `Windows 10 Pro` | 旧兼容值，不能据此把系统误判为 Windows 10 |
| Visual Studio | Community 2026 `18.8.2` | 已核实 |
| MSBuild | `18.8` | 已核实 |
| MSVC v143 | `14.44.35207` | 已安装，项目基线 |
| MSVC v145 | `14.51.36231` | 已安装，但不作为 M1.0 基线 |
| Windows SDK | `10.0.22621.0`、`10.0.26100.0`、`10.0.28000.0` | 已安装 |
| .NET Host | `9.0.17` | 已核实 |
| .NET SDK | 系统安装无；便携 `10.0.302` 已验证 | 系统 `dotnet --list-sdks` 无结果；`C:\tmp\winwidgetboard-dotnet-10.0.302` 可输出 `10.0.302` 并提供 .NET/WindowsDesktop Runtime `10.0.10` |

Windows 版本应综合 `DisplayVersion`、`CurrentBuildNumber`、`UBR`、Edition 和系统版本信息判断。`ProductName` 可能为兼容目的保留旧字符串，不能单独作为 Windows 10/11 的判定依据。

## 3. 项目锁定值

| 层级 | 锁定值 | 落盘位置 | 选择理由 |
| --- | --- | --- | --- |
| .NET SDK | `10.0.302` | `global.json` | 固定 restore、build 和 test 使用的 SDK，防止开发机自动漂移 |
| .NET 目标 | `.NET 10` | C# 项目文件与中央构建属性 | 与已批准架构基线一致 |
| Windows App SDK Runtime | `2.3.1` stable | `Directory.Packages.props` | 锁定 Windows App SDK 运行时组件 |
| Windows App SDK WinUI | `2.3.2` | `Directory.Packages.props` | 只引用普通测试窗口需要的 WinUI 组件，避免完整元包引入未使用的 AI/ML/Widgets |
| Windows SDK API | `10.0.26100.0` | C++/C# 项目目标属性 | 作为当前 CI 和 Windows 11 兼容基线；不因本机存在 28000 SDK 而自动提高最低构建基线 |
| Windows SDK BuildTools | `10.0.26100.8249` | `Directory.Packages.props` | 固定 Windows SDK 构建资产，避免按机器安装状态浮动 |
| MSVC PlatformToolset | `v143` | C++ 项目属性 | 采用较广泛可用的 CI/兼容基线，不自动切换到本机更新的 v145 |
| MSVC 工具版本 | `14.44.35207` | 工具链记录和 CI 镜像说明 | 与选定 v143 安装对应 |
| C++ 标准 | C++23 | C++ 项目属性 | 与架构基线一致 |
| 测试宿主 | `Microsoft.NET.Test.Sdk` `18.8.1` | `Directory.Packages.props` | 固定测试发现与执行版本 |
| 测试框架 | `MSTest` `4.3.3` | `Directory.Packages.props` | 使用官方元包并固定测试框架版本 |
| 首个构建架构 | x64 | `.sln` 和项目配置 | 仅用于 M1 技术验证，不构成最终平台承诺 |

选择 `10.0.26100.0` 和 v143 的目标是让本地与 CI 共享可重复的兼容基线，而不是追随本机已安装的最高版本。升级 Windows SDK、MSVC、Windows App SDK Runtime/WinUI 或 .NET SDK 必须单独修改锁定文件、重新 restore/build/test，并记录兼容性结果。

## 4. 构建先决条件

执行 M1.0 构建前必须满足：

1. Windows 11 x64 开发环境。
2. Visual Studio Community 2026 `18.8.2`，安装 MSBuild、使用 C++ 的桌面开发组件、MSVC v143 `14.44.35207` 和 Windows SDK `10.0.26100.0`。
3. 安装 x64 .NET SDK `10.0.302`，且 `dotnet --version` 在仓库目录中输出 `10.0.302`。
4. 能访问 NuGet.org，或已配置包含全部锁定包的受信任离线源。
5. 使用普通用户权限；restore、build 和 test 不得要求管理员权限。
6. 从 Visual Studio Developer PowerShell 运行，以确保 `msbuild.exe` 解析到 Visual Studio 2026 的 MSBuild `18.8`。

如果 `global.json` 已存在而本机没有 `10.0.302`，`dotnet` 命令应失败并提示缺失 SDK。这是正确的版本锁定行为，不得通过删除 `global.json`、启用浮动版本或改用 9.0 Host 绕过。

## 5. 环境核对命令

在 PowerShell 中运行：

```powershell
Set-Location 'D:\Projects\WinWidgetBoard'

$windowsVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$windowsVersion |
    Select-Object ProductName, DisplayVersion, EditionID, CurrentBuildNumber, UBR

where.exe dotnet
dotnet --info
dotnet --list-sdks
dotnet --version

where.exe msbuild
msbuild -version
```

预期关键结果：

- `CurrentBuildNumber` 与 `UBR` 组合为 `26200.8737`；
- `ProductName` 即使显示 `Windows 10 Pro`，也不得覆盖 25H2 和 build 26200 的实际系统判断；
- `dotnet --version` 必须为 `10.0.302`；
- `msbuild -version` 必须为 `18.8`；
- `where.exe msbuild` 应指向 Visual Studio Community 2026 安装目录，而不是未知或旧版工具链。

可用以下命令检查已安装的 Windows SDK 和 MSVC 工具目录：

```powershell
Get-ChildItem -LiteralPath "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -Directory |
    Select-Object -ExpandProperty Name

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsInstallPath = & $vswhere -latest -products * -property installationPath

Get-ChildItem -LiteralPath (Join-Path $vsInstallPath 'VC\Tools\MSVC') -Directory |
    Select-Object -ExpandProperty Name
```

## 6. Restore、Build 与 Test

前置条件满足后，在 Visual Studio Developer PowerShell 中完整执行：

```powershell
Set-Location 'D:\Projects\WinWidgetBoard'

dotnet --version
msbuild -version

dotnet restore .\WinWidgetBoard.sln

msbuild .\WinWidgetBoard.sln `
    /m `
    /p:Configuration=Debug `
    /p:Platform=x64

msbuild .\WinWidgetBoard.sln `
    /m `
    /p:Configuration=Release `
    /p:Platform=x64

dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj `
    -c Release `
    --no-restore

git diff --check
git status --short
```

验收时应保存：

- `dotnet --info` 与 `msbuild -version` 输出；
- restore 的包源和成功结果；
- Debug x64、Release x64 构建结果；
- `UT-BUILD-001 [BLD-001]` 测试结果；
- Release 输出路径；
- `git diff --check` 和最终工作树清单。

日常临时验证不必手工重复以上步骤：`scripts/Start-DevSandbox.ps1` 按本文锁定值包装第 5、6 节，
准备便携 SDK 环境、校验运行中的 SDK 与 `global.json` 一致、并探测两种托管输出布局。

```powershell
.\scripts\Start-DevSandbox.ps1                 # 环境核对
.\scripts\Start-DevSandbox.ps1 -Task Build     # 托管项目用 dotnet，LauncherHost 用 MSBuild
.\scripts\Start-DevSandbox.ps1 -Task Test -Filter "FullyQualifiedName~ResponsiveGridLayoutTests"
.\scripts\Start-DevSandbox.ps1 -Task Run       # 隔离沙箱数据启动 Broker 与面板，结束后清理
```

`-Task Run` 使用独立实例身份和系统临时目录下的数据目录，只停止自己启动的进程，不使用生产数据；
包含任务栏入口的完整真实流程仍用 `scripts/Run-WinWidgetBoard.ps1`。

## 7. 当前验证边界

截至 2026-08-06 的本地验收结果：

- 本机操作系统、Visual Studio、MSBuild、MSVC 和已安装 Windows SDK 已核实；
- 官方便携 .NET SDK `10.0.302` 的下载校验通过；
- WorkspacePanel 与 UnitTests 的 locked-mode restore 通过；
- Contracts、UnitTests 和 WorkspacePanel 的 Debug/Release x64 构建均为 0 警告、0 错误；
- LauncherHost 使用 Visual Studio MSBuild `18.8` 完成 Debug/Release x64 构建；
- `UT-BUILD-001 [BLD-001]` 在 Debug 和 Release 各通过 1 次；
- LauncherHost 与 WorkspacePanel 的 Debug/Release `--smoke-test` 均返回 0；
- WorkspacePanel Release 的真实顶层窗口已创建，标题资源正确加载，并在标准关闭消息后以退出码 0 结束；
- WorkspacePanel 的 `--smoke-test` 已实际构造并关闭 MainWindow，可回归覆盖 XAML 资源初始化；
- LauncherHost Release 动态依赖仅包含 Win32 和 MSVC/UCRT 运行库，不包含 WinUI、SQLite、HTTP 或插件运行时；
- WorkspacePanel 明确采用“.NET framework-dependent、Windows App SDK self-contained”，运行时需要 .NET 10。
- M2.0.2 已在便携 SDK `10.0.302` 下完成 Debug/Release UnitTests `31/31`、CoreBroker/WorkspacePanel 构建和真实 Release CoreBroker + LauncherHost `session.hello`/`session.ping` smoke；
- XML/JSON、双语资源键、解决方案清单、禁入依赖和尾随空白检查通过，`git diff --check` 通过。

尚未通过的边界：

- 系统安装 SDK 的干净用户环境与 GitHub Actions 尚未运行；
- 便携 SDK 未注册到 Visual Studio SDK resolver，因此本机的单命令混合 `.sln` 构建未通过；Contracts、CoreBroker、UnitTests、WorkspacePanel 和 LauncherHost 已按锁定工具链分别完成 Debug/Release 构建；
- WorkspacePanel 普通可见窗口的自动化启动/关闭已通过，人工目视内容与布局尚未验收；
- 任务栏集成、点击穿透、自动隐藏、Explorer 重启、多显示器、DPI 和动效属于后续 M1 工作包，尚未验证。

因此当前可以表述为“M1.0 实现完成，本地分项目构建与自动 smoke-test 通过”，不能表述为“干净环境或 CI 全部验收通过”。
