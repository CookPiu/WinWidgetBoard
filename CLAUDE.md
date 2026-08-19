# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Conventions

- All prose docs (`README.md`, `AGENTS.md`, `docs/`) are written in Chinese. Code identifiers, comments, and commit messages are English.
- Commits: `type(scope): summary`, one purpose per commit. Default branch `main`.
- `AGENTS.md` is the binding execution-rules file for this repo and applies to every directory. Read it before non-trivial work; the sections below summarize the parts that shape day-to-day commands and code structure.

### Files another agent owns — read, never write

This project was developed with Codex before this session. Its configuration is **not** mine to edit:

- `.codex/` (`config.toml`, `agents/*.toml`) — Codex runtime configuration;
- `AGENTS.md` — the binding execution-rules file, authored and maintained on the Codex side.

Read both freely; `AGENTS.md` outranks this file when they disagree, and a rule that belongs in `AGENTS.md`
gets restated here instead of edited there. If a change genuinely requires touching either, stop and ask the user first.
`CLAUDE.md` is mine to maintain — new standing constraints go here.

## Toolchain (version-locked — do not float)

`global.json` pins .NET SDK `10.0.302` with `rollForward: disable`. If `dotnet` fails because the SDK is missing, that is the intended behavior — **never** fix it by deleting `global.json`, enabling roll-forward, or falling back to the 9.0 host.

There is no system-installed .NET SDK on the reference machine. A portable SDK lives at `C:\tmp\winwidgetboard-dotnet-10.0.302`; managed builds and the PowerShell test scripts expect it via `DOTNET_ROOT` / `DOTNET_ROOT_X64`:

```powershell
$env:DOTNET_ROOT = 'C:\tmp\winwidgetboard-dotnet-10.0.302'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

Other locked values (`Directory.Packages.props`, project files): Windows App SDK Runtime `2.3.1` / WinUI `2.3.2`, Windows SDK API `10.0.26100.0`, MSVC `v143`, C++23, MSBuild `18.8` (VS Community 2026), MSTest `4.3.3`. Only `x64` is configured. Raising any of these is a deliberate change to the lock files plus a re-verified restore/build/test — not a side effect.

`Directory.Build.props` sets `TreatWarningsAsErrors=true` and `RestorePackagesWithLockFile=true` (each project has a `packages.lock.json`; CI restores in locked mode). A new warning fails the build.

## Build

**The single-command `.sln` build does not work locally.** LauncherHost is C++/MSBuild and needs Visual Studio's MSBuild `18.8`; the portable .NET SDK is not registered with the VS SDK resolver. Build per project instead. CI (`.github/workflows/build.yml`) uses a machine with both installed and does build the whole solution.

```powershell
dotnet restore .\WinWidgetBoard.sln

# Managed projects (portable SDK is fine)
dotnet build .\src\CoreBroker\WinWidgetBoard.CoreBroker.csproj -c Release
dotnet build .\src\WorkspacePanel\WinWidgetBoard.WorkspacePanel.csproj -c Release

# LauncherHost — from a Visual Studio Developer PowerShell
msbuild .\src\LauncherHost\WinWidgetBoard.LauncherHost.vcxproj /p:Configuration=Release /p:Platform=x64

# Whole solution (Developer PowerShell, CI-style)
msbuild .\WinWidgetBoard.sln /m /p:Configuration=Release /p:Platform=x64
```

Output paths differ between the C++ and C# projects, and scripts hard-code them:

| Project | Path under `artifacts\bin\` |
| --- | --- |
| LauncherHost | `WinWidgetBoard.LauncherHost\<Config>\x64\` |
| WorkspacePanel | `WinWidgetBoard.WorkspacePanel\x64\<Config>\net10.0-windows10.0.26100.0\win-x64\` |
| CoreBroker | `WinWidgetBoard.CoreBroker\<Config>\net10.0-windows10.0.26100.0\win-x64\` |

Note the inconsistency: managed projects gain an extra `x64\` segment when `Platform` is passed explicitly (as the solution build and CI do), and lack it otherwise — which is why the same executable can exist at two paths. `Run-WinWidgetBoard.ps1` expects WorkspacePanel **with** that segment and CoreBroker **without** it. If a script throws `Missing runtime file`, compare its hard-coded path against what actually got produced rather than rebuilding blindly.

`artifacts/` is gitignored. Do not create parallel output dirs (`test-build-final`, `order-fix-final`, …); use `%TEMP%` when a process lock forces isolation, and clean up afterward.

## Test

```powershell
# Full suite (current Release baseline: 306/306)
dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj -c Release --no-restore

# A single test class or method
dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj -c Release --no-restore `
    --filter "FullyQualifiedName~ResponsiveGridLayoutTests"
```

CI instead runs `--no-build --no-restore --property:Platform=x64`, matching its explicit `msbuild /p:Platform=x64` build. Don't mix the two forms: `--no-build` plus a `Platform` that differs from how the assembly was built looks in the wrong output directory and reports a missing assembly rather than a test failure.

Tests carry traceability IDs in their MSTest `DisplayName`, e.g. `UT-GRID-001 [LYT-001] Effective width selects 2, 4 or 6 columns`. Docs and status reports cite those IDs; grep the ID to find the method, then filter by its class or method name.

### Built-in smoke tests (exit code 0 = pass)

Each executable self-tests without a desktop session — CI runs the first two:

- `WinWidgetBoard.LauncherHost.exe`: `--smoke-test`, `--geometry-smoke-test`, `--panel-launch-smoke-test`, `--panel-lifecycle-smoke-test`, `--corebroker-smoke-test`
- `WinWidgetBoard.WorkspacePanel.exe`: `--smoke-test` (constructs and closes a real WinUI `MainWindow`), `--broker-smoke-test`
- `WinWidgetBoard.CoreBroker.exe`: `--pipe-handshake-smoke-test`

### Real-desktop UI Automation

`scripts/Test-*.ps1` drive the real Release x64 panel with `SendInput` and UIA. They all import `scripts/WinWidgetBoard.UiAutomation.psm1` — the shared module for window/element lookup, input, and process lifecycle. **Never copy helper functions into a new script**; extend the module.

```powershell
.\scripts\Test-CardDragInteraction.ps1 -WithBroker
.\scripts\Test-CardDragInteraction.ps1 -ResizeDragCombination   # cross-card drag + resize regression
.\scripts\Test-NoteMarkdownPreviewInteraction.ps1
```

They need an interactive, focusable desktop and refuse to run alongside an existing WorkspacePanel.

Write-path acceptance runs **must** isolate process identity and data: `--acceptance-test --test-instance-id <new guid> --data-directory <strict subdir of %TEMP%>`, kill only the processes the script started, delete the temp dir at the end, and never touch the production database. Do not change production single-instance or default data paths to make a test easier (ADR 0017).

### Run the app

```powershell
.\scripts\Start-DevSandbox.ps1                 # environment check: SDK, MSBuild, which exes are built
.\scripts\Start-DevSandbox.ps1 -Task Build     # managed projects via dotnet, LauncherHost via MSBuild if present
.\scripts\Start-DevSandbox.ps1 -Task Test -Filter "FullyQualifiedName~ResponsiveGridLayoutTests"
.\scripts\Start-DevSandbox.ps1 -Task Run       # broker + panel on sandbox data, hold until Enter, then clean up
```

`Start-DevSandbox.ps1` is the ad-hoc testing entry point: it primes the portable SDK, verifies the running SDK matches `global.json` (and refuses to proceed rather than working around the lock), probes both managed output layouts so the extra-`x64`-segment trap can't bite, and for `-Task Run` isolates everything — temp broker data directory, temp `LOCALAPPDATA`, fresh acceptance instance ID — deleting it afterward only once the path is confirmed to sit inside the system temp dir. It stops only the processes it started. Use `-KeepTestData` to inspect the sandbox DB afterward.

```powershell
.\scripts\Run-WinWidgetBoard.ps1     # full real flow incl. taskbar entry; needs Release x64 builds of all three exes
```

It generates a session token, starts CoreBroker hidden, points `WINWIDGETBOARD_WORKSPACE_PANEL` at the panel, then runs LauncherHost in the foreground. Unlike the sandbox, this one uses **production** data.

## Architecture

Three processes and two shared libraries. The process split is load-bearing, not organizational — see `docs/03-technical-architecture.md` and ADR 0002.

```
User → LauncherHost (C++/Win32)  ──spawns──→  WorkspacePanel (C#/WinUI 3)
                │                                      │
                └──── minimal native pipe client ──────┤ CoreBroker.Client (typed clients)
                                                       │
                                              CoreBroker (C# worker)
                                                  ├── SQLite (winsqlite3.dll)
                                                  └── Open-Meteo (HTTPS)
```

Boundaries that must not erode:

- **LauncherHost** never links WinUI, SQLite, HTTP, or a managed UI runtime. It talks to CoreBroker through its own minimal native client (`src/LauncherHost/CoreBrokerClient.cpp`, ADR 0008). No DLL injection, hooks, or private Explorer XAML/visual-tree names; when taskbar geometry is ambiguous it hides or degrades rather than guessing.
- **WorkspacePanel** never opens SQLite and never hand-assembles low-level envelopes. It goes through `CoreBrokerSession` (`src/WorkspacePanel/Ipc/`), which owns handshake, heartbeat, reconnect, and exposes typed clients: `.Notes`, `.Layout`, `.Cards`, `.WeatherSettings`.
- **CoreBroker** never references WinUI. It validates every IPC input and owns the database, network access, and scheduling. Queues, retries, timeouts, and releases are all bounded.
- Process wiring flows through env vars: `WINWIDGETBOARD_COREBROKER_SESSION_TOKEN` (or `--session-token`) and `WINWIDGETBOARD_WORKSPACE_PANEL`. Tokens must never reach logs.

### IPC

Named pipe, current-user ACL, protocol `1.0`. Frame = 4-byte little-endian length + UTF-8 JSON, max 1 MiB, rejected before allocation. `session.hello` gates every business method. Requests, responses, and `cards.snapshot` events share one ordered writer. Full method list, limits, and the stable error codes are in `docs/07-api-contracts.md` — treat those error-code strings as a compatibility surface.

Every write carries a `clientOperationId`: same ID + same payload replays the first result, same ID + different payload returns `validation.invalid-argument`.

Each domain guards concurrency with its own token, and each has a distinct conflict code:

| Domain | Token | Conflict |
| --- | --- | --- |
| Layout | `expectedRevision` | `conflict.layout-revision` |
| Notes | `expectedUpdatedAtUtc` | `conflict.notes-revision` |
| Weather settings | `expectedRevision` | `conflict.weather-settings-revision` |

### Card snapshot pipeline

One direction only, immutable snapshots, per-connection subscription:

```
Provider → ProviderRefreshScheduler → ProviderCardSnapshotAdapter
  → CardSnapshotSubscriptionHub → cards.snapshot event
  → CoreBrokerCardsClient → CardSnapshotSubscriptionCoordinator → CardSnapshotDispatcher → card UI
```

The UI applies a snapshot only if its `sequence` is higher, and never infers actions or permissions from a missing payload. Backpressure is bounded (queue coalesced per instance, ~32 pending instances) and a slow client is disconnected rather than queued without limit (ADR 0019). Panel visibility drives refresh pausing through `ProviderRefreshVisibilityRegistry`.

### Layout

Logical 2/4/6-column grid. Persisted state is `order`, `sizeId` (`s`/`m`/`l`/`w`/`xl`), and optional `preferredColumn`/`preferredRow` — **never screen pixels** (ADR 0003, 0014). Placement is deterministic first-fit; the panel saves once when an edit session completes, with 20-step undo/redo.

Stable-identity invariants (`docs/02-ux-design-spec.md` §4.3) have caused real regressions and are enforced by tests:

- placement/order changes update the existing `CardSurfaceItem` **in place**; only membership changes may rebuild or rebind the `ItemsRepeater` data source;
- drag and resize resolve their target from the currently realized repeater element, never from a possibly stale root `Tag`;
- a resize step is computed from that card's own current size, not the previously touched card's.

Touching `CardGridLayout`, `CardLayoutSurfaceViewModel`, repeater binding, drag, or resize requires the real cross-card "drag + resize in one edit session" regression (`-ResizeDragCombination`).

### Weather

Open-Meteo is the only network provider (`app.winwidgetboard.weather.open-meteo`, 15-minute visible cadence, 10-second deadline). Requests carry label, lat/long, and units — no account, device ID, or auto-location. Location label and coordinates persist to SQLite; **weather payloads never do** — they stay in the broker process, and failures degrade to Offline/Stale/Error while keeping the last in-process success (ADR 0020, 0021). Saving a location swaps the provider request key at runtime via `WeatherProviderRuntime` and publishes a Loading snapshot.

## The unit-test project links WorkspacePanel sources

WorkspacePanel is a WinUI app and cannot be `ProjectReference`d, so `tests/UnitTests/WinWidgetBoard.UnitTests.csproj` pulls its files in individually with `<Compile Include="..\..\src\WorkspacePanel\...">`. The test project references no Windows App SDK package, which makes the split enforceable:

- **WinUI-free** logic (ViewModels, `ResponsiveGridLayout`, `CardDragController`, motion controllers, `CardRuntime*`, coordinators, formatters) is linked in and unit-tested.
- **WinUI-bound** types (`CardGridLayout`, template selectors, value converters, `SurfaceMotionCoordinator`, `MainWindow`) are not linked and cannot be.

So: **a new testable WorkspacePanel type must be added to that `ItemGroup` by hand, and must not touch `Microsoft.UI.*` / `Windows.*`.** If it needs WinUI, either keep the logic in a WinUI-free type the code-behind calls, or accept that it is only covered by a real-desktop script.

The same csproj also copies `App.xaml`, `MainWindow.xaml`, `WorkspaceVisualStyles.xaml`, and both `.resw` files into `TestAssets/` via `<None … CopyToOutputDirectory>`. Contract tests (`WorkspaceVisualFoundationContractTests`, `CardRuntimeStatusUiContractTests`, `CardTemplateDragContractTests`, `CardVisibilityUiContractTests`) parse that XAML as XML to assert resource keys, template structure, and automation IDs. New XAML that needs this treatment needs a `<None>` entry too.

## Scope guardrails

The project deliberately shrank to a "lightweight core" of five capabilities (ADR 0022): taskbar entry, panel, basic responsive layout, local notes, weather + manual location. Timers, todos, clipboard, calendar, system monitoring, plugins, accounts, cloud sync, and telemetry are **deferred** — existing placeholder cards and base contracts stay as-is and must not be extended without explicit re-approval. Default rejections: new card types, new provider/plugin platforms, abstractions for a hypothetical second implementation, performance rewrites without measurements.

Three files are known debt and must not absorb unrelated responsibilities — extract a coordinator or domain handler first, then add behavior: `src/WorkspacePanel/MainWindow.xaml.cs` (~2150 lines), `src/CoreBroker/Providers/ProviderRefreshScheduler.cs` (~1690), `src/CoreBroker/Commands/CoreBrokerCommandRouter.cs` (~730). XAML code-behind holds only view events, focus, and coordination; state belongs in a ViewModel or service.

## Verification: pick the risk tier, don't run everything

From `AGENTS.md` §5 and `docs/08-testing-strategy.md`. Do not run the full matrix for a small change, and do not claim desktop/performance behavior without evidence.

| Tier | Change | Required |
| --- | --- | --- |
| **L0** | Docs, comments, behavior-free config | `git diff --check`, link/format check. No build. |
| **L1** | Pure logic, ViewModels, DTO mapping, I/O-free policy | Related unit tests; build the affected project if needed. |
| **L2** | XAML/window interaction, IPC, SQLite, provider network, process start/reconnect, taskbar hit-testing | Related tests + Release build or smoke of the affected exe + **one** real desktop flow targeting this specific risk. |
| **L3** | Release candidate only, when the user says so | Clean restore, full solution Debug/Release, all tests, all smokes, multi-monitor + 100–200% DPI, high contrast / reduced motion / text scaling / keyboard, startup + background CPU/working set + soak, DB backup-restore, install/update/signing. |

Bug fixes need a minimal repro or regression. Performance claims require device, OS build, configuration, debugger state, duration, tool, and raw numbers.

## UI changes

`docs/02-ux-design-spec.md` (v0.2, "静谧画布") is the authority for structure, density, visuals, and motion; read it before any visible UI work and use `docs/templates/ui-change-template.md` as the checklist (normally not committed). Reviews record `Before / After / Why` against a fixed reference environment (theme, DPI, window size, language, data state, baseline commit).

Practical rules that get violated most often:

- Reuse tokens and styles from `src/WorkspacePanel/Styles/WorkspaceVisualStyles.xaml`. No second palette/spacing/type/radius/shadow system in a page. Spacing follows the 4-DIP `WwbSpace*` scale.
- Colors come from `ThemeResource` / Windows semantic colors — no hard-coded RGB. State is never conveyed by color alone.
- Normal themes use borderless layering; high contrast restores the system 1-DIP boundary. Don't outline every container.
- Search boxes, single button groups, and steady-state status must not occupy their own permanent row. Low-frequency actions use progressive disclosure; content space wins.
- Animate only compositor properties (`Opacity`, `Scale`, `Translation`) — never `Width`/`Height`/`Margin`. Every animation must be interruptible, must resume from the current displayed value on reversal, and must degrade under reduced motion. High-frequency click/type/drag/resize gets zero added latency. No `RepositionThemeTransition` in the card grid: it breaks 1:1 pointer projection during drag.
- Every icon button needs a localized tooltip, a readable automation name, and a stable unique `AutomationId` — the UIA scripts depend on those IDs. Hit targets stay logical (32 DIP header buttons); never hard-code physical pixels.
- User-visible strings go in **both** `src/WorkspacePanel/Strings/en-US/Resources.resw` and `zh-CN/Resources.resw`; they must stay in sync (205 entries each today).

## Documentation

One authoritative location per fact — the rule that most doc defects here violate. Updating docs is part of the change, not a follow-up: when behavior, scope, or a contract moves, update the one owning document in the same commit, and only that one.

Who owns what:

| Fact | Authority |
| --- | --- |
| Current scope of the five capabilities | `docs/01-product-requirements.md` |
| UI structure, density, visuals, motion | `docs/02-ux-design-spec.md` |
| Processes, IPC, storage, complexity budget | `docs/03-technical-architecture.md` |
| IPC methods, limits, error codes | `docs/07-api-contracts.md` |
| Risk tiers and what each requires | `docs/08-testing-strategy.md` |
| Attack surface and privacy constraints | `docs/09-security-privacy.md` |
| Current facts, debt, next steps, **and all pass/warning counts and desktop-flow evidence** | `docs/status/implementation-status.md` |
| Locked tool versions / dependency licenses | `docs/development/` |
| Source-tree map and per-project boundaries | `src/README.md` (thin pointer — architecture stays in `docs/03`) |

Rules that follow from it:

- Never copy a test count, warning count, or verification result into a second document. Those live in the status page only; everywhere else links to it. A number duplicated into a strategy or reference doc goes stale silently and is later read as current.
- The status page is current state, not a changelog. Replace superseded entries; don't append a per-change log — Git history covers what was done.
- ADRs (`docs/adr/`) are only for expensive, hard-to-reverse decisions. Never rewrite an accepted ADR's conclusion — add a new one, mark the old superseded, and update the index. Stale requirement IDs inside an old ADR (`SET-003`, `NFR-PRI-003` …) are historical record, so leave them.
- Ordinary features and fixes get **no** work-package document. Per-work-package prose (`M2.3.6 已增加…`) is the pattern this repo deliberately dropped; don't reintroduce it in any README.
- Adding a doc under `docs/` means adding its row to `docs/README.md`. That index is the discovery path — a doc missing from it is effectively invisible.
- Prose stays Chinese; keep the existing tone (short declarative clauses, `；`-separated lists, tables over paragraphs) and the `文档状态 / 版本 / 日期` header block when a doc already has one.
- Docs-only changes are L0: `git diff --check` plus a link check. Verify every relative path you write actually resolves; don't cite a file or symbol without confirming it exists.
