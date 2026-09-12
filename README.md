# WinWidgetBoard

[![Build](https://github.com/CookPiu/WinWidgetBoard/actions/workflows/build.yml/badge.svg)](https://github.com/CookPiu/WinWidgetBoard/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

English | [简体中文](README.zh-CN.md)

A local-first widget board for Windows 11. It adds its own entry to the taskbar strip and opens
a native half-screen panel holding notes, weather, hardware readings and AI token usage.

It does **not** inject into `explorer.exe`, hook the shell, or read Explorer's private XAML tree.
The entry is an ordinary Win32 window of our own, positioned from public geometry — when that
geometry is ambiguous it hides or degrades rather than guessing.

## What it does

| Capability | Detail |
| --- | --- |
| **Taskbar entry** | A native C++/Win32 info strip embedded in the taskbar. Alignment, content, an optional hardware capsule, and a hotkey (four presets plus a recorded custom one) are all configurable. |
| **Panel** | WinUI 3, Desktop Acrylic, system accent layering. Closing hides it and keeps the process resident, so reopening costs tens of milliseconds rather than a cold start. |
| **Responsive layout** | A logical 2/4/6-column grid. Drag, resize, add and remove cards in an edit session; 20-step undo/redo. Persisted state is order and a size ID — never screen pixels — so a layout survives a resolution or DPI change. |
| **Notes** | Local CRUD, search, Markdown preview, autosave with a draft-preserving failure policy. |
| **Weather** | Current conditions from **Open-Meteo** (default, no account) or **QWeather** (your own API host and key). Location comes from Windows device location with foreground consent, or from a manual search. Metric/imperial is a display setting. |
| **Hardware monitor** | Nine readings from public user-mode APIs (PDH counters; no kernel driver is shipped or loaded). Temperature and fan speed are read from **HWiNFO** or **Core Temp** shared memory when one of them is running; a reading with no source is not displayed at all rather than shown as zero. |
| **Token usage** | Reads the local session transcripts of **Claude Code** and **Codex**, read-only, and shows today's spend, an hourly spend curve, billed/output/cache tokens and a cache hit rate. Cost is estimated from public API list prices and marked `≈`. Transcript content never enters logs or the database. |

### Not in scope

Timers, todos, clipboard history, calendar, third-party plugins, accounts, cloud sync and telemetry
are deliberately deferred ([ADR-0022](docs/adr/0022-lightweight-core-strategy.md)). Placeholder cards
that exist in the tree are layout filler and will not grow behavior. Feature requests for these are
answered in `CONTRIBUTING.md` rather than silently queued.

## Privacy

- Notes, layout, weather location and settings live in a SQLite database under
  `%LOCALAPPDATA%\WinWidgetBoard`. There is no account and no cloud sync.
- There is **no telemetry**. Nothing is sent anywhere unless a capability you enabled needs it.
- Outbound traffic is limited to: the selected weather source's endpoints, and — if you leave daily
  price sync on — the LiteLLM public price table on `raw.githubusercontent.com`.
- A location search term is the only user-typed text that leaves the machine, and only when you
  submit the search.
- Weather payloads are never persisted; they stay in the broker process.
- The QWeather API key is the product's only user credential. It is DPAPI-encrypted at rest, sent
  only as a per-request header to allow-listed vendor domains, never placed in a URL or a log, and
  write-only over IPC.
- Session transcripts are read but never copied, stored or transmitted.

Details: [docs/09-security-privacy.md](docs/09-security-privacy.md).

## Requirements

- Windows 11 x64, build 22000 or newer. Older taskbars are not a supported target — the entry
  refuses to embed there and falls back to a safe slot in the work area.
- [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0). The two
  managed processes are framework-dependent; the Windows App SDK is bundled self-contained, so
  there is nothing else to install.
- Optional: HWiNFO or Core Temp running, if you want temperature and fan readings.
- Normal user rights. No elevation, no service, no driver, no registry classes.

## Install

Download the zip from [Releases](https://github.com/CookPiu/WinWidgetBoard/releases), extract it
anywhere, and run `WinWidgetBoard.LauncherHost.exe`. It starts the panel and the broker itself.

> The binaries are **not code-signed**, so SmartScreen will warn on first run. Verify the download
> against the release page if that matters to you, or build from source.

To remove it: quit from the entry's context menu, delete the folder, and delete
`%LOCALAPPDATA%\WinWidgetBoard` if you also want the data gone.

## Build from source

Prerequisites are version-locked on purpose — see
[docs/development/toolchain.md](docs/development/toolchain.md) for the full baseline and why each
value is pinned. In short: Visual Studio 2026 with the C++ desktop workload (MSVC v143, Windows SDK
`10.0.26100.0`) and .NET SDK `10.0.302`, which `global.json` pins with `rollForward: disable`.

```powershell
dotnet restore .\WinWidgetBoard.sln

# From a Visual Studio Developer PowerShell
msbuild .\WinWidgetBoard.sln /m /p:Configuration=Release /p:Platform=x64

dotnet test .\tests\UnitTests\WinWidgetBoard.UnitTests.csproj -c Release --no-build --property:Platform=x64
```

Then install your build for daily use:

```powershell
.\scripts\Install-WinWidgetBoard.ps1
```

Each executable also self-tests without a desktop session — `--smoke-test` on the launcher and the
panel, `--pipe-handshake-smoke-test` on the broker — and exits 0 on success. That is what CI runs.
Per-project build commands, the output-path trap between the C++ and C# projects, and the
real-desktop UI Automation scripts are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## Architecture

Three processes and two shared libraries. The split is load-bearing, not organizational: the
resident entry must never carry a UI or network runtime, and the panel must never touch the
database directly.

```
User -> LauncherHost (C++/Win32)  --spawns-->  WorkspacePanel (C#/WinUI 3)
             |                                        |
             +---- minimal native pipe client --------+ CoreBroker.Client (typed clients)
                                                      |
                                             CoreBroker (C# worker)
                                                 +-- SQLite (winsqlite3.dll)
                                                 +-- weather / pricing over HTTPS
```

They talk over a current-user named pipe: a 4-byte length prefix plus UTF-8 JSON, capped at 1 MiB
and rejected before allocation, with `session.hello` gating every business method. Every write
carries a `clientOperationId` so a retry replays the first result instead of applying twice, and
each domain has its own concurrency token and conflict code.

Rationale lives in [docs/03-technical-architecture.md](docs/03-technical-architecture.md); the wire
contract in [docs/07-api-contracts.md](docs/07-api-contracts.md).

## Status

Pre-1.0, single-maintainer, actively developed. The seven capabilities above are implemented and
run end to end, but verification so far comes from one reference machine — the full display matrix
(multi-monitor, 100–200% DPI, high contrast, text scaling) and the multi-device performance gates in
[docs/08-testing-strategy.md](docs/08-testing-strategy.md) have not been run.

Current facts, known debt and next steps:
[docs/status/implementation-status.md](docs/status/implementation-status.md).

## Documentation

All design documentation is written in Chinese and indexed at [docs/README.md](docs/README.md).
Start with [the ADR index](docs/adr/README.md) if you want to know why something is the way it is —
every expensive decision has one.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) first. It covers the risk-tiered verification rules, the
scope boundary, and what the build actually needs. Security issues go through
[SECURITY.md](SECURITY.md), not the issue tracker.

## License

[MIT](LICENSE) — see [ADR-0039](docs/adr/0039-mit-license-and-public-repository.md) for the decision
and its dependency constraints. No third-party source or assets are vendored into this repository.
