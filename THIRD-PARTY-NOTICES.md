# Third-party notices

WinWidgetBoard itself is licensed under the [MIT License](LICENSE), and **no third-party source
code or assets are vendored into this repository** — see
[ADR-0006](docs/adr/0006-source-reuse-and-license.md) and
[ADR-0039](docs/adr/0039-mit-license-and-public-repository.md).

The release archive does, however, redistribute binaries produced by Microsoft. They are listed
below with the exact version that was built against and the terms they are distributed under. This
file ships inside the release archive; the authoritative dependency record, including build-only
packages, is [docs/development/dependencies.md](docs/development/dependencies.md).

Nothing listed here is licensed under GPL, AGPL, Anti-996, or any other term that would place an
obligation on a downstream user of WinWidgetBoard beyond Microsoft's own.

---

## 1. Windows App SDK / WinUI 3

Deployed self-contained (`WindowsAppSDKSelfContained=true`), so the runtime files sit next to
`WinWidgetBoard.WorkspacePanel.exe` instead of being installed machine-wide.

| Package | Version |
| --- | --- |
| `Microsoft.WindowsAppSDK.Runtime` | 2.3.1 |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.2 |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.5 |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.3 |

Redistributed files include `Microsoft.WinUI.dll`, `Microsoft.UI.*.dll`, `Microsoft.Windows.*.dll`,
the matching `.winmd` projections, `MRM.dll`, `CoreMessagingXP.dll`, `DwmSceneI.dll`,
`Microsoft.Internal.FrameworkUdk.dll`, the `Microsoft.UI.Xaml\` folder, and the per-language
resource folders under `WorkspacePanel\`.

© Microsoft Corporation. Distributed under the **Microsoft Software License Terms — Microsoft
Windows App SDK**, available on each package's NuGet page, for example
<https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.3.1/License>.

## 2. Microsoft Edge WebView2

| Package | Version |
| --- | --- |
| `Microsoft.Web.WebView2` | 1.0.3719.77 |

Redistributed files: `Microsoft.Web.WebView2.Core.dll`,
`Microsoft.Web.WebView2.Core.Projection.dll`.

WinWidgetBoard does not use WebView2 — it arrives as a transitive dependency of WinUI 3 and is
copied by the self-contained deployment. © Microsoft Corporation, distributed under the
**Microsoft Edge WebView2 SDK** terms:
<https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License>.

## 3. .NET projection for the Windows SDK

| Component | Version |
| --- | --- |
| `Microsoft.Windows.SDK.NET.Ref` (runtime pack) | 10.0.26100.57 |

Redistributed files: `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`.

Supplied by the .NET SDK's Windows targeting pack rather than by a direct package reference.
© Microsoft Corporation; distributed under the Microsoft terms that accompany the .NET SDK and
the Windows SDK .NET projection:
<https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.26100.57/License>.

## 4. Microsoft Visual C++ runtime

| Component | Version |
| --- | --- |
| `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, `MSVCP140.dll` | see `sbom.cdx.json` in the archive |

`WinWidgetBoard.LauncherHost.exe` is compiled with `/MD` against the v143 toolset
(`14.44.35207`), so it imports these three DLLs. The copies that ship are taken from the
redistributable that accompanies the build machine's Visual Studio installation — a later 14.x
build than the compiler's own, which is what the runtime's binary compatibility is for — and the
exact file versions are recorded in the SBOM rather than restated here, where they would go stale.

They are redistributed **application-local**, next to the executable, which is the deployment the
Visual Studio redistributable terms permit; WinWidgetBoard does not install or modify any
machine-wide runtime. © Microsoft Corporation. See the "Distributable Code" section of the Visual
Studio license terms: <https://visualstudio.microsoft.com/license-terms/>.

The Universal C RT (`api-ms-win-crt-*`) is a Windows component and is **not** redistributed.

---

## Not redistributed

These are required or used at runtime but are **not** part of the release archive:

- **.NET 10 Desktop Runtime (x64)** — the two managed processes are framework-dependent. The user
  installs it from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **`winsqlite3.dll`** — the SQLite build that ships as a Windows system component. WinWidgetBoard
  binds to the system copy and redistributes no SQLite binary or source.
- **HWiNFO, Core Temp** — optional sensor sources. WinWidgetBoard reads the shared memory they
  publish when one of them is already running. Neither is bundled, installed, or required.
- **Build- and test-only packages** — `Microsoft.Windows.SDK.BuildTools`,
  `Microsoft.Windows.SDK.BuildTools.MSIX`, `Microsoft.NET.Test.Sdk`, `MSTest` and their transitive
  dependencies never enter the product output.

## Network services

The product contacts these services only when the corresponding capability is enabled. It
redistributes nothing from them; their terms govern your use of the service, not this software.

- **Open-Meteo** (`api.open-meteo.com`, `geocoding-api.open-meteo.com`) — default weather source,
  no account. <https://open-meteo.com/en/terms>
- **QWeather / 和风天气** (`*.qweatherapi.com`) — optional second weather source, used only with
  your own API host and key. <https://dev.qweather.com>
- **LiteLLM public price table** (`raw.githubusercontent.com`) — optional daily sync of public
  model list prices, used to estimate token cost. MIT licensed.
  <https://github.com/BerriAI/litellm>
