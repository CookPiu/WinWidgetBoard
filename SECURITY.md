# Security Policy

## Supported versions

This is a pre-1.0, single-maintainer project. Only the latest release and the current `main` receive
fixes. There are no backports to older tags.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/CookPiu/WinWidgetBoard/security/advisories/new)
(Security tab → Advisories → Report a vulnerability). That channel is private to the maintainer
until an advisory is published.

Useful to include:

- affected component — `LauncherHost`, `WorkspacePanel`, `CoreBroker`, or a script;
- version or commit, and your Windows build;
- what an attacker gains, and what access they need to start;
- a minimal reproduction, and a crash dump or log excerpt if you have one — **redact any API key,
  session token, note content or location data before attaching anything**.

Expect an acknowledgement within a week. There is no bounty program. Fixes ship on `main` and in the
next release; credit in the advisory if you want it.

## Threat model

[docs/09-security-privacy.md](docs/09-security-privacy.md) is the authority on the attack surface and
the data that must be protected. In summary, the product is entirely current-user and local:

- a named pipe with a current-user ACL, protocol-versioned, with every frame length-checked and
  rejected before allocation, and `session.hello` gating every business method;
- a SQLite database under `%LOCALAPPDATA%`, holding notes, layout, weather location and settings;
- the QWeather API key, DPAPI-encrypted at rest, sent only as a per-request header to allow-listed
  vendor domains — never in a URL, a log, or an IPC response;
- outbound HTTPS to the selected weather source and, optionally, the LiteLLM public price table;
- read-only access to local session transcripts and to HWiNFO / Core Temp shared memory;
- no elevation, no service, no kernel driver, no Explorer injection, no telemetry.

Things that are **especially** worth reporting: anything that gets a session token, API key or note
content into a log, a crash dump, a command line or a URL; anything that lets a process outside the
current user's context reach the pipe; a frame that is allocated before it is validated; a path that
sends user-typed text off the machine without an explicit submit; an unbounded queue, retry or
timeout; a taskbar geometry path that covers a system control instead of degrading.

## Out of scope

- **Attacks that already require code execution as the same user.** The product is current-user by
  design: an attacker at that level can read the database directly, with or without this product.
- **Unsigned binaries and the resulting SmartScreen warning.** Known and documented in the README;
  releases are not code-signed today.
- **Trusting HWiNFO / Core Temp shared memory.** Reading a sensor block another program published is
  the documented design ([ADR-0033](docs/adr/0033-hwinfo-shared-memory-sensor-source.md),
  [ADR-0034](docs/adr/0034-core-temp-as-a-second-sensor-source.md)). A malformed block must not crash
  us or produce a wrong reading without a status — **that** part is in scope.
- Vulnerabilities in Windows itself, in the .NET runtime, or in a third-party weather service.
- Results from an automated scanner with no demonstrated impact.
