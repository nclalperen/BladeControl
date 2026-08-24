# BladeControl

![BladeControl](assets/readme-header.png)

Fan, performance and thermal control for the Razer Blade, as a normal Windows application: a
privileged runtime service that owns the hardware, and a small desktop panel that talks to it
over a local typed IPC channel.

> **BladeControl is not a Razer product.** It is an independent, unofficial project, not
> produced, endorsed, sponsored or supported by Razer Inc. "Razer" and "Razer Blade" are
> trademarks of Razer Inc., used here only to identify the hardware this software targets.

---

## Supported hardware

**Validated reference platform: Razer Blade 16 — RZ09-0483.**

Every hardware claim in this repository was verified on that model. The Razer HID protocol is
implemented from observed behaviour, and command layout, fan verification semantics and
performance-level encodings differ across Blade generations.

| | |
|---|---|
| **Validated** | Razer Blade 16, RZ09-0483 |
| **Unvalidated** | Every other Razer model, including other Blade 16 SKUs |

BladeControl does **not** claim universal Razer compatibility. On an unvalidated model it may
misidentify state or refuse to operate; the failure modes are designed to be safe, not to be
absent. Closed-loop thermal control refuses to start unless the runtime can independently
establish an authoritative CPU package temperature, which is the check most likely to stop an
unvalidated machine — deliberately.

Running on unvalidated hardware is at your own risk. See [Safety architecture](#safety-architecture).

## What it does

- **Performance profiles** — Balanced, Silent, and Custom with hardware-validated CPU/GPU
  levels. Applied through the firmware, one typed request at a time, never retried
  automatically.
- **Fan control** — firmware Auto, or fixed targets from 2000–5000 RPM in 100 RPM steps.
  Dragging a slider performs no hardware write; only an explicit Apply does.
- **Closed-loop thermal control** — a runtime-owned curve with hysteresis and rate limiting,
  gated behind a fresh authoritative qualification before it will enter Manual fan mode.
- **Live telemetry** — CPU package temperature, power and utilisation, and GPU temperature and
  clocks, read through the runtime's providers, with in-memory history graphs. Nothing is
  written to disk. GPU power and utilisation are not among them: NVML refuses those two to the
  service, and they are reported as unavailable rather than guessed — see
  [known limitations](docs/known-limitations.md).
- **Diagnostics** — provider qualification, PawnIO provenance, watchdog observations,
  scheduler statistics and the runtime event stream.

## The compact panel

The compact panel is the normal daily-use surface: 400 px wide, placed at the bottom-right of
the monitor under the cursor, DPI- and multi-monitor-aware, hidden from the taskbar, dismissed
with `Escape`.

```
┌──────────────────────────────────────┐
│ BladeControl                    ↗  × │
│ ● Online                             │
│                                      │
│  CPU        FIRMWARE AUTO      GPU   │
│  61.5 °C          —         52.0 °C  │
│              firmware owns           │
│                 cooling              │
│ ┌──────────────────────────────────┐ │
│ │ PERFORMANCE                      │ │
│ │ [ Balanced ] Silent    Custom    │ │
│ │ CPU  [Low][Med][High][Bst][OC]   │ │
│ │ GPU  [Low][Medium][High]         │ │
│ └──────────────────────────────────┘ │
│ ┌──────────────────────────────────┐ │
│ │ COOLING                          │ │
│ │ [  Auto   ]  Fixed    Dynamic    │ │
│ └──────────────────────────────────┘ │
│ ⌄ Settings                           │
│ ● Firmware Auto   Diagnostics  Full  │
└──────────────────────────────────────┘
```

The middle column is the one worth explaining. **There is no measured fan speed on this
machine** — the controller's fan register echoes the last commanded target, LibreHardwareMonitor
exposes no fan sensors, NVML reports fan speed unavailable, and `Win32_Fan` leaves its speed
fields empty. So the panel shows the *target* under Fixed and Dynamic, labelled as a target, and
under firmware Auto it shows nothing at all: there is no BladeControl target then, and the last
one is a leftover from a mode you have already left. The heading changes to `FIRMWARE AUTO` to
say who is in charge.

The full CPU (Low, Medium, High, Boost) and GPU (Low, Medium, High) range is selectable.
Overclock is the one level this build will not send — it is withheld so BladeControl cannot
interfere with tuning done in XTU — and it appears greyed rather than hidden, with the reason
on its tooltip. An absent control looks like hardware that lacks the feature; a disabled one
tells the truth. A machine already sitting in Overclock is still read and reported accurately.

One fan control is offered, not two. The machine has two fans and the runtime addresses and
verifies each zone separately, but setting them independently is a decision with no basis and an
easy way to desynchronise them.

The full sidebar application remains available as the **Advanced / Full App** surface — same
shell, same connection, created only when asked for. Both share one runtime connection and one
polling loop.

## Installation

Download `BladeControl-<version>-win-x64.msi` from the releases page, run it, and accept the
single elevation prompt. That installs:

- the application under `%ProgramFiles%\BladeControl\`;
- the **BladeControl Runtime** Windows service, started automatically with Windows (delayed
  start, so hardware initialisation does not compete with the boot storm);
- the diagnostic CLI under `%ProgramFiles%\BladeControl\Diagnostics\` (the installer does not
  add it to the machine-wide `PATH`);
- a Start Menu shortcut and normal Installed Apps metadata;
- sign-in launch for the installing user, which can be turned off in the panel's Settings or
  from Task Manager's Startup tab.

You never need PowerShell, and the user interface itself never asks for elevation — it is a
thin IPC client with no hardware access of its own.

**Verify your download.** Pre-release builds are unsigned, so Windows shows an
unknown-publisher warning. Check the SHA-256 hash against `SHA256SUMS.txt` published with the
release. See [docs/code-signing.md](docs/code-signing.md).

**Uninstall** from Installed Apps. It stops the runtime through its safe shutdown path, removes
the service and the installed files, and leaves your preferences in
`%LocalAppData%\BladeControl\` in case you reinstall. It does not remove PawnIO, NVIDIA drivers,
or any other component BladeControl merely used.

A [portable archive](docs/portable-build.md) is also published. It does **not** install or start
the service and is intended for developers.

### PawnIO

BladeControl does not bundle PawnIO, and does not download or install it. Without it, fan and
performance control work normally; closed-loop thermal control is refused, because the
authoritative CPU package temperature it depends on is unavailable. The installer detects
PawnIO and reports the dependency state rather than acting on your behalf.

The full reasoning — licensing, kernel-driver trust, and why bundling would weaken a validated
safety property — is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Safety architecture

The design principle is that the runtime is the only component that touches hardware, and it
refuses to act when it cannot prove the preconditions hold.

- **Single hardware owner.** The runtime service owns Razer HID, the embedded controller, and
  the telemetry providers. A machine-wide singleton is taken before any device is opened, so a
  second runtime host cannot start alongside the service.
- **The GUI has no hardware path.** `BladeControl.UI` references neither the hardware provider
  assembly nor the service host, and this is enforced by a test rather than by convention.
- **Qualification before Manual mode.** Entering Manual fan control requires a fresh
  authoritative qualification: CPU package temperature present and provenance-safe, GPU
  telemetry healthy, deterministic GPU selection, Razer HID available. Any failure refuses the
  transition and says which check failed.
- **Provenance, not assumption.** PawnIO's driver image is verified by Authenticode or Windows
  catalog trust, with publisher subject and file hash recorded. Failed verification disables
  authoritative CPU telemetry and therefore thermal control.
- **Emergency handoff.** On fault or loss of telemetry the runtime returns the fans to firmware
  Auto rather than holding a stale manual target.
- **Safe shutdown.** A clean SCM stop, `Ctrl+C`, uninstall or an upgrade all run the same path:
  restore firmware fan mode, drain the event stream within a bound, stop any thermal session
  exactly once. Hiding or exiting the user interface never stops thermal control.
- **A hardware-control channel with a real ACL.** The IPC pipe grants access to locally
  logged-on users, denies network and anonymous callers, and refuses to let unprivileged
  processes impersonate the runtime. See [docs/ipc-security.md](docs/ipc-security.md).

## Diagnostic limitations

Stated plainly, because the alternative is implying precision that does not exist:

- **Firmware-reported fan values (`0x0D81`) are not claimed to be physical tachometer RPM.**
  They are reported as *firmware-reported fan state and value*, labelled as such throughout the
  interface, and never presented as "actual RPM". Whether they correspond to a measured
  tachometer reading has not been established on the reference platform. See
  [docs/protocol/max-fan-research.md](docs/protocol/max-fan-research.md).
- **After a thermal session stops**, watchdog, telemetry-health and scheduler readings describe
  the finished session, not current firmware state. The interface relabels them as last-session
  observations and mutes them, so a successfully stopped session does not read as a live fault.
- **Only the immutable default thermal curve is usable**, because the runtime does not yet
  expose typed save/select for user curves.
- **Custom curves cannot be saved.** The runtime serves `GetThermalCurve` but has no typed
  save or select operation, so the default curve is the only one available.

Further known backend limits are recorded in [docs/gui-backend-needs.md](docs/gui-backend-needs.md).

## Building from source

Requires the .NET 8 SDK on Windows x64. For the installer, the WiX 5 CLI:

```bash
dotnet tool install --global wix --version 5.0.2
```

Build and test:

```bash
dotnet test BladeControl.sln -c Release
```

Produce every release artifact — publish trees, MSI, portable archive, symbols and hashes:

```powershell
powershell -ExecutionPolicy Bypass -File build/pack.ps1
```

`pack.ps1` runs the test and formatting gates first and refuses to package if either fails. It
never installs the MSI, registers a service, or touches hardware.

Run the runtime in the foreground for development (elevated prompt required):

```bash
BladeControl.Service.exe console --verbose
```

Stop the installed service first — only one host may own the hardware.

### Repository layout

| Project | Role |
|---|---|
| `BladeControl.Razer` | Razer HID protocol: command IDs, encodings, verification |
| `BladeControl.Telemetry` | Telemetry contracts and models |
| `BladeControl.Thermal` | Curve interpolation, hysteresis, rate limiting |
| `BladeControl.Runtime` | Runtime core, state machine, typed IPC contracts |
| `BladeControl.Hardware.Windows` | Windows providers (LibreHardwareMonitor, NVML, HID) |
| `BladeControl.Ipc` | Pipe endpoint identity and access-control policy |
| `BladeControl.Service` | Windows service host — the sole hardware owner |
| `BladeControl.UI` | WPF compact panel and advanced application |
| `BladeControl.Cli` | Diagnostics and development command line |
| `installer/` | WiX 5 MSI |

## Documentation

| Document | Contents |
|---|---|
| [docs/release-notes-v0.1.3.md](docs/release-notes-v0.1.3.md) | What is in this release and what it deliberately does not claim |
| [docs/safety-model.md](docs/safety-model.md) | What the software will and will not do to your cooling, and why |
| [docs/known-limitations.md](docs/known-limitations.md) | Measured limits, unavailable data, and what is not yet done |
| [docs/engineering-log.md](docs/engineering-log.md) | Chronological record of changes, evidence and rejected hypotheses |
| [docs/runtime-core-v1.md](docs/runtime-core-v1.md) | Runtime state machine and IPC contract |
| [docs/thermal-control-v1.md](docs/thermal-control-v1.md) | Curve, hysteresis and scheduler semantics |
| [docs/gui-v0.1.md](docs/gui-v0.1.md) | Interface contract, both surfaces |
| [docs/ipc-security.md](docs/ipc-security.md) | Pipe threat model and ACL |
| [docs/install-test-checklist.md](docs/install-test-checklist.md) | Manual installer validation on a reference machine |
| [docs/code-signing.md](docs/code-signing.md) | Where Authenticode signing belongs |
| [docs/license-recommendation.md](docs/license-recommendation.md) | Licence analysis and recommendation |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Dependency and licence audit |
| [SECURITY.md](SECURITY.md) | Reporting a vulnerability |

## Licence

**GNU General Public License v3.0 or later** (`GPL-3.0-or-later`). See [LICENSE](LICENSE).

Per-file headers are deliberately not used. The project has a single copyright holder and ships
`LICENSE` with every artifact — the MSI installs it, the portable zip carries it, and the
installer presents the GPL text — which is what sections 4 through 6 require. Headers would help
most if code were copied out of the project file by file; that is worth revisiting if the project
gains contributors, and is recorded here so the omission is a choice rather than an oversight.

BladeControl is free software: you may redistribute and modify it under the terms of the GPL as
published by the Free Software Foundation, either version 3 or (at your option) any later
version. It is distributed in the hope that it will be useful, but **without any warranty** —
see the [warning](#warning) below and sections 15 and 16 of the licence.

Every shipped dependency is compatible: MPL-2.0 (LibreHardwareMonitorLib and friends),
Apache-2.0 (HidSharp, one-way compatible with GPL-3.0), and MIT (.NET runtime,
`Microsoft.Extensions.*`). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the audit
and [docs/license-recommendation.md](docs/license-recommendation.md) for why GPL-3.0 was chosen
over the alternatives.

PawnIO is GPL-2.0-or-later and is **not** redistributed — it remains an external dependency the
user installs separately.

## Warning

This software controls cooling hardware. It is provided without warranty of any kind. A defect,
an unvalidated hardware model, or a misconfiguration can lead to sustained high temperatures.
The authors accept no liability for hardware damage, data loss or any other harm.
