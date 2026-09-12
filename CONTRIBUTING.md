# Contributing to WinWidgetBoard

Thanks for looking. This is a single-maintainer project with a deliberately small scope, so the
most useful thing this document can do is tell you up front what gets merged and what does not.

Issues and pull requests are welcome in **English or Chinese**. The design documentation under
`docs/` is Chinese only; you will need it for anything beyond a small fix.

## Scope: read this before writing code

The project shrank to a "lightweight core" on purpose
([ADR-0022](docs/adr/0022-lightweight-core-strategy.md)), since re-approved up to seven
capabilities: taskbar entry, panel, responsive layout, notes, weather, hardware monitor, token usage.

**Default rejections**, regardless of how good the implementation is:

- new card types, or new behavior on the existing placeholder cards;
- a plugin or provider platform for third-party extensions
  ([ADR-0004](docs/adr/0004-plugin-security-boundary.md), and §2 of
  [ADR-0030](docs/adr/0030-token-usage-card.md) for why this stayed rejected);
- timers, todos, clipboard history, calendar, accounts, cloud sync, telemetry;
- abstractions introduced for a hypothetical second implementation;
- performance rewrites without measurements;
- anything that ships or loads a kernel driver
  ([ADR-0029](docs/adr/0029-drop-the-bundled-sensor-driver.md)).

**Always welcome**: bug fixes with a repro, correctness and robustness fixes, honest degradation on
machines unlike the reference one (different sensors, taskbar shapes, locales, DPI), accessibility
fixes, documentation corrections, and translations of user-visible strings.

**Open an issue first** for anything that changes behavior, the IPC contract, the database schema, a
locked tool version, or the UI. A PR that arrives without that conversation may be a lot of work
thrown at a decision that was already made in an ADR.

## Boundaries that must not erode

These are architectural, not stylistic. A change that crosses one will be sent back:

- `LauncherHost` never links WinUI, SQLite, HTTP or a managed UI runtime, and never uses DLL
  injection, hooks, or private Explorer XAML names. It reaches CoreBroker through its own minimal
  native pipe client ([ADR-0008](docs/adr/0008-launcher-corebroker-client.md)).
- `WorkspacePanel` never opens SQLite and never hand-assembles low-level envelopes; it goes through
  `CoreBrokerSession` and its typed clients.
- `CoreBroker` never references WinUI. It validates every IPC input and owns the database, the
  network and the scheduling. Queues, retries, timeouts and releases stay bounded.
- Persisted layout is order plus a size ID — never screen pixels
  ([ADR-0003](docs/adr/0003-responsive-card-grid.md),
  [ADR-0014](docs/adr/0014-logical-layout-cell-replay.md)).
- Session tokens and API keys never reach a log, a URL, or a command line.

`AGENTS.md` is the binding execution-rules file for this repository and applies to every directory.
`CLAUDE.md` carries the same rules plus the traps that have already cost someone a debugging round.
Read both if you are doing anything non-trivial — including if you are working with an AI agent,
which both files are written for.

## Build

Prerequisites and the reasoning behind every locked value:
[docs/development/toolchain.md](docs/development/toolchain.md). Nothing here floats — `global.json`
pins .NET SDK `10.0.302` with `rollForward: disable`, and `Directory.Packages.props` pins the rest.
If `dotnet` fails because the SDK is missing, that is the lock working. Do not "fix" it by deleting
`global.json` or falling back to another SDK.

`Directory.Build.props` sets `TreatWarningsAsErrors=true` and `RestorePackagesWithLockFile=true`.
A new warning is a build failure, and a dependency change must update the project's
`packages.lock.json` — CI restores in locked mode.

```powershell
dotnet restore .\WinWidgetBoard.sln

# Whole solution, the way CI does it, from a Visual Studio Developer PowerShell
msbuild .\WinWidgetBoard.sln /m /p:Configuration=Release /p:Platform=x64
```

Building per project also works and is faster while iterating:

```powershell
dotnet build .\src\CoreBroker\WinWidgetBoard.CoreBroker.csproj -c Release
dotnet build .\src\WorkspacePanel\WinWidgetBoard.WorkspacePanel.csproj -c Release -p:Platform=x64
msbuild .\src\LauncherHost\WinWidgetBoard.LauncherHost.vcxproj /p:Configuration=Release /p:Platform=x64
```

**The output-path trap.** Managed projects gain an extra `x64\` path segment when `Platform` is
passed explicitly and lack it otherwise, so the same executable can exist at two paths:

| Project | Path under `artifacts\bin\` |
| --- | --- |
| LauncherHost | `WinWidgetBoard.LauncherHost\<Config>\x64\` |
| WorkspacePanel | `WinWidgetBoard.WorkspacePanel\x64\<Config>\net10.0-windows10.0.26100.0\win-x64\` |
| CoreBroker | `WinWidgetBoard.CoreBroker\<Config>\net10.0-windows10.0.26100.0\win-x64\` |

`Install-WinWidgetBoard.ps1` expects the panel **with** that segment and the broker **without** it.
If a script reports a missing runtime file, compare its hard-coded path against what was actually
produced before rebuilding blindly. `artifacts/` is gitignored; do not create parallel output
directories.

`scripts\Start-DevSandbox.ps1` is the ad-hoc entry point: it checks the environment, builds, tests,
or runs the whole product against an isolated temp database and cleans up afterwards.

## Test

```powershell
dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj -c Release --no-restore
dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj -c Release --no-restore `
    --filter "FullyQualifiedName~ResponsiveGridLayoutTests"
```

Tests carry traceability IDs in their MSTest `DisplayName`, e.g.
`UT-GRID-001 [LYT-001] Effective width selects 2, 4 or 6 columns`. Grep the ID to find the method.

**The unit-test project links WorkspacePanel sources file by file** (`<Compile Include="..\..\src\
WorkspacePanel\...">`), because a WinUI app cannot be `ProjectReference`d. The test project
references no Windows App SDK package, which makes the split enforceable: a new testable panel type
must be added to that `ItemGroup` by hand and must not touch `Microsoft.UI.*` / `Windows.*`. If it
needs WinUI, keep the logic in a WinUI-free type that the code-behind calls.

Every executable self-tests without a desktop session and exits 0 on success — this is what CI runs:

- `WinWidgetBoard.LauncherHost.exe --smoke-test` (also `--geometry-smoke-test`,
  `--entry-visual-smoke-test`, `--panel-launch-smoke-test`, `--panel-lifecycle-smoke-test`,
  `--corebroker-smoke-test`)
- `WinWidgetBoard.WorkspacePanel.exe --smoke-test`, `--broker-smoke-test`
- `WinWidgetBoard.CoreBroker.exe --pipe-handshake-smoke-test`

`scripts\Test-*.ps1` drive the real Release x64 panel with `SendInput` and UI Automation. Run them
from **`pwsh`**, never Windows PowerShell 5.1, which is DPI-unaware and will misreport every window
rectangle at non-100% scaling. They need an interactive desktop and refuse to run alongside an
existing WorkspacePanel — including an installed one, which you must quit first. They all import
`scripts\WinWidgetBoard.UiAutomation.psm1`; **extend that module rather than copying helpers into a
new script**.

Acceptance runs must isolate process identity and data
(`--acceptance-test --test-instance-id <guid> --data-directory <subdir of %TEMP%>`), kill only what
they started, and never touch the production database
([ADR-0017](docs/adr/0017-acceptance-process-and-data-isolation.md)).

## How much verification your change needs

[docs/08-testing-strategy.md](docs/08-testing-strategy.md) is the authority. The short version:

| Tier | Change | Required |
| --- | --- | --- |
| **L0** | Docs, comments, behavior-free config | `git diff --check` and a link check. No build. |
| **L1** | Pure logic, ViewModels, DTO mapping, I/O-free policy | Related unit tests; build the affected project. |
| **L2** | XAML/window interaction, IPC, SQLite, provider network, process lifecycle, taskbar hit-testing | Related tests, a Release build or smoke of the affected executable, and **one** real-desktop flow targeting that specific risk. |
| **L3** | Release candidates only | The full matrix: clean restore, whole solution, all tests and smokes, multi-monitor and 100–200% DPI, accessibility passes, soak and footprint, backup-restore, install/update. |

Do not run the full matrix for a small change, and do not claim desktop or performance behavior you
did not observe. A performance claim needs device, OS build, configuration, debugger state,
duration, tool and raw numbers. A bug fix needs a minimal repro or a regression test.

Touching `CardGridLayout`, `CardLayoutSurfaceViewModel`, repeater binding, drag or resize requires
the cross-card regression: `.\scripts\Test-CardDragInteraction.ps1 -ResizeDragCombination`. Touching
the card fold additionally requires `.\scripts\Test-CardFoldHitTest.ps1`.

## UI changes

[docs/02-ux-design-spec.md](docs/02-ux-design-spec.md) is the authority for structure, density,
visuals and motion. Read it before any visible change and use
[docs/templates/ui-change-template.md](docs/templates/ui-change-template.md) as the checklist
(normally not committed). The rules broken most often:

- Reuse tokens and styles from `src/WorkspacePanel/Styles/WorkspaceVisualStyles.xaml`. No second
  palette, spacing, type, radius or shadow system. Spacing follows the 4-DIP `WwbSpace*` scale.
- Colors come from `ThemeResource` or Windows semantic colors. No hard-coded RGB, and state is never
  conveyed by color alone.
- Animate only compositor properties (`Opacity`, `Scale`, `Translation`) — never `Width`, `Height`
  or `Margin`. Every animation must be interruptible, resume from the displayed value on reversal,
  and degrade under reduced motion.
- Every icon button needs a localized tooltip, a readable automation name and a stable
  `AutomationId`; the UIA scripts depend on those IDs.
- User-visible strings go in **both** `src/WorkspacePanel/Strings/en-US/Resources.resw` and
  `zh-CN/Resources.resw`, and the two files' entry counts must match.

## Documentation

One authoritative location per fact. When behavior, scope or a contract moves, update the one owning
document in the same commit — and only that one. `docs/README.md` lists who owns what. Never copy a
test count, warning count or verification result into a second document; those live on the status
page and everywhere else links to it.

ADRs are only for expensive, hard-to-reverse decisions. Never rewrite an accepted ADR's conclusion:
add a new one, mark the old superseded, and update the index.

## Pull requests

- Branch from `main`; `main` is the default and release branch.
- Commit messages: `type(scope): summary`, imperative, English, one purpose per commit.
- No merge commits inside a PR — rebase onto `main`.
- State in the PR description what you verified and at which tier, with the actual command output.
  "Should work" is not a verification result; saying you skipped a check is fine, claiming one you
  did not run is not.
- CI (Debug and Release, x64) must be green. Markdown-only changes skip CI by design.
- By opening a pull request you agree that your contribution is licensed under the
  [MIT License](LICENSE). There is no CLA.

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).
