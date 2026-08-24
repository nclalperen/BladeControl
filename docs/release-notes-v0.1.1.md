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

The licence is conveyed with the binaries, as GPL-3.0 sections 4 through 6 require: the MSI
installs `LICENSE.txt`, the portable zip carries it, and the installer's licence dialog presents
the GPL text rather than a summary.

---

## Open decisions before tagging

**0. The GPU thermal signature — resolved.** Dynamic no longer refuses. Thermal control now runs
in whatever performance mode the machine is in, and qualifies against that mode's limits:
Balanced 87/89/92, Silent and Custom 75/77/80. Verified live, including a full session run in
Silent + Manual with the machine never moved to Balanced.

The signature pins the T.Limit offsets, which are static device properties, and enumerates the
anchors observed on the part. It used to pin the derived limits, which pinned the anchor with
them — and the anchor is the driver's current thermal target, not a device property.

Two things follow, both documented in [known-limitations.md](known-limitations.md): changing
performance mode during a session hands the fans back to firmware, because the limits in force
were derived for the previous mode; and an anchor that has not been observed on that part is
refused even when it looks reasonable, which can turn away a legitimate configuration.

The remaining four are the copyright holder's call, not an engineering task. None of them blocks
the build; all of them are easier to settle before a tag than after.

**1. Licence variant — settled: `GPL-3.0-or-later`.** Declared once in
`Directory.Build.props` as `PackageLicenseExpression`, so every built assembly carries it, and
stated in the README. Per-file headers are deliberately not used: a single copyright holder, and
`LICENSE` ships with every artifact, which is what sections 4 through 6 require. Worth revisiting
if the project gains contributors. The original text of this decision follows.

**2. Contributor licensing — moot for now.** There are no contributors besides the copyright
holder, so no CLA or DCO is needed. Worth adding a DCO if that changes; nothing is blocked by its
absence today.

**3. Pre-release or full release.** Everything here was validated on exactly one machine, a
Razer Blade 16 (RZ09-0483). Qualification fails closed on anything else, so other hardware is
refused rather than mishandled — but "works on one laptop" is what a pre-release tag
communicates honestly and a `v0.1.0` tag does not.

**4. Cold-boot validation — done.** A real reboot was performed. The service starts unaided
(delayed auto-start, Running by 166 s), the machine came back in Custom + Auto, qualification
derived Custom's own limits, a session ran as Custom + Manual, and stop restored the booted
state. No crash events, no duplicate host, and Dynamic did not resume by itself.
