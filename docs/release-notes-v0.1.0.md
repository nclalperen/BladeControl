# BladeControl v0.1.0 — release notes

First release. Fan, performance and thermal control for the Razer Blade 16, as a Windows
service that owns the hardware plus a desktop panel that talks to it over local IPC.

**Read [known limitations](known-limitations.md) before installing.** The short version: this is
validated on exactly one laptop model, physical fan RPM is not available on that hardware, and
closed-loop control refuses to start on hardware it cannot verify.

---

## What it does

- **Performance profiles** — Balanced, Silent, and Custom with hardware-validated CPU/GPU
  levels. Levels that are known to the protocol but unvalidated on this device are visibly
  disabled rather than failing when applied.
- **Fan control** — firmware Auto, or fixed targets from 2000–5000 RPM in 100 RPM steps.
- **Dynamic Cooling** — a closed control loop on a 500 ms cadence, driving fan targets from a
  temperature curve, with a graded thermal safety ladder for both CPU and GPU.
- **Diagnostics** — provenance, scheduler timing distributions, protocol exchange history, and
  an authoritative thermal-ownership verdict.

## Safety model in one paragraph

BladeControl borrows fan control from firmware, holds it only while it can continuously prove it
still has it, and gives it back the moment anything is uncertain. It never modifies firmware,
never disables a thermal protection, and never reaches outside the documented Razer HID command
set. The CPU's own throttling and the GPU's hardware slowdown and shutdown remain in force
throughout. Full detail in [the safety model](safety-model.md).

## Requirements

| | |
|---|---|
| OS | Windows 11 x64 |
| Hardware | Razer Blade 16 (RZ09-0483) — see limitations for other models |
| Dependency | **PawnIO**, installed separately. Not bundled. |
| Privileges | Administrator, for service installation and control |

PawnIO's driver is Authenticode-verified before its readings are trusted for any control
decision. Without it, closed-loop thermal control will not start; everything else still works.

## Thermal policy

| CPU | Action |
|---|---|
| ≥ 90 °C | maximum validated fan target, keep control |
| ≤ 85 °C × 3 | release the override |
| ≥ 95 °C × 3 | hand back to firmware Auto |
| ≥ 100 °C × 1 | hand back immediately |

| GPU (RTX 4090 Laptop: 75 / 77 / 80 °C) | Action |
|---|---|
| ≥ 75 °C | maximum validated fan target, keep control |
| ≤ 72 °C × 3 | release the override |
| ≥ 77 °C × 3 | hand back to firmware Auto |
| ≥ 79 °C × 1 | hand back immediately |

GPU thresholds come from limits the device reports about itself, accepted only when they match a
signature validated against real hardware. **An unrecognised GPU gets no thresholds and Dynamic
Cooling refuses to start.** There is no generic fallback — a wrong assumed threshold is worse
than none, because it looks like protection.

## What this release deliberately does not claim

- **Actual fan RPM.** Razer `0x0D81` returns the commanded target echoed back. Four read-only
  sources were examined on the validated hardware and none exposes a tachometer, so actual speed
  is reported as unavailable rather than shown as a number that would agree with itself whatever
  the fans were doing.
- **Compatibility beyond one model.** Every hardware claim here comes from a single machine.
- **That an emergency handoff is a failure.** It is the protection working, and it is presented
  as distinct from a fault, where cooling ownership is genuinely uncertain.

## Known behaviour worth expecting

- A cycle that writes a fan target exceeds the 500 ms control period by roughly 125 ms. The
  schedule self-corrects and loses no periods; worst-case detection delay for a newly critical
  temperature is about 1.1 s. Accepted and documented rather than fixed — see
  [known limitations](known-limitations.md).
- After an emergency handoff the runtime latches and will not resume on its own. Recovery is a
  deliberate restart, because an automatic retry after a thermal event is how you get a loop.
- Uninstalling preserves your settings.

## Licence

GPL-3.0. See [LICENSE](../LICENSE).
