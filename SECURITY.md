# Security policy

## Reporting a vulnerability

**Do not open a public issue for a security vulnerability.**

Use GitHub's private vulnerability reporting on this repository (Security → Report a
vulnerability). If that is unavailable, contact the maintainer through the address on their
GitHub profile and mention that the report is security-sensitive.

Please include: what an attacker gains, the privilege level they need to start, affected
version, and a reproduction if you have one. A rough report of a real problem is far more
useful than a polished report of a theoretical one.

Expect an acknowledgement within a week. BladeControl is a small project without a paid
security team; there is no bounty, and there is no guaranteed remediation window.

## What is in scope

BladeControl is privileged software: the runtime service runs as LocalSystem and drives fan,
performance and embedded-controller hardware. Reports that matter most:

- **Local privilege escalation** — any path by which a non-administrator gains code execution
  or arbitrary hardware control through the runtime service, its IPC channel, or the installer.
- **IPC boundary bypass** — reaching `\\.\pipe\BladeControl.Runtime.v1` from a context the
  documented ACL is meant to exclude (network logon, anonymous, non-interactive service
  account), or getting the user interface to trust a pipe it should refuse. The intended model
  is in [docs/ipc-security.md](docs/ipc-security.md); a demonstrated gap between that document
  and the implementation is a valid report.
- **Safety-gate bypass** — driving the hardware into Manual fan mode, or holding a manual fan
  target, without passing the runtime's qualification, or defeating emergency handoff. These
  gates exist to prevent a thermal event; bypassing them is a security issue, not just a bug.
- **Installer issues** — unquoted service paths, writable install directories, DLL or EXE
  planting, or elevation reachable through the MSI beyond the single intended prompt.
- **Supply-chain issues** in what BladeControl actually redistributes (see
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## What is out of scope

- **An interactive user changing fan or performance settings.** This is the product working as
  designed. The pipe ACL deliberately grants locally logged-on users write access, because they
  are the physical operators of the machine — they already control its power button. The ACL is
  the "who may ask" boundary, not the thermal-safety boundary.
- **An administrator doing administrator things.** Anyone who is already an administrator can
  stop the service, replace the binaries, or load their own driver.
- **PawnIO, LibreHardwareMonitor, NVML and the NVIDIA driver.** BladeControl redistributes none
  of these; report vulnerabilities in them upstream. A flaw in *how BladeControl uses* them —
  for example accepting an unverified PawnIO driver image — is in scope here.
- **Unsigned pre-release builds triggering SmartScreen.** Known and documented; see
  [docs/code-signing.md](docs/code-signing.md).
- **Missing hardening that has no reachable impact**, absent a concrete attack path.

## Supported versions

| Version | Supported |
|---|---|
| `0.1.x` | Yes — current development line |
| earlier | No |

Pre-1.0 BladeControl has no long-term support branches. Fixes land on the current line.

## Disclosure

Coordinated disclosure preferred. Once a fix is available and released, credit is given in
`CHANGELOG.md` unless the reporter prefers otherwise. If a report is declined as out of scope,
the reasoning is given rather than silence.

## Security-relevant design already documented

Before reporting, these may answer the question:

- [docs/ipc-security.md](docs/ipc-security.md) — pipe threat model, ACL, anti-squatting
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — the PawnIO trust and provenance decision
- [docs/runtime-core-v1.md](docs/runtime-core-v1.md) — runtime state machine and safety gates
- [README.md](README.md#safety-architecture) — safety architecture overview
