# BladeControl v0.1.3 — release notes

Bug-fix release over v0.1.1. Fan, performance and thermal control for the Razer Blade 16, as a
Windows service that owns the hardware plus a desktop panel that talks to it over local IPC.

**Read [known limitations](known-limitations.md) before installing.** The short version: this is
validated on exactly one laptop model, physical fan RPM is not available on that hardware, and
closed-loop control refuses to start on hardware it cannot verify.

---

## What it does

- **Performance profiles** — Balanced, Silent, and Custom, with the full CPU (Low/Medium/
  High/Boost) and GPU (Low/Medium/High) level range selectable in Custom. Overclock is
  deliberately excluded from both, so BladeControl cannot interfere with tuning done in XTU; a
  machine already sitting in Overclock is still read and reported accurately, it simply cannot
  be selected here.
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

GPU thresholds are mode-dependent: thermal control runs in whatever performance mode the machine
is already in, and the GPU limits it qualifies against follow that mode's own thermal target
(RTX 4090 Laptop):

| Mode | keep control ≥ | release ≤ × 3 | firmware Auto ≥ × 3 | immediate handoff ≥ × 1 |
|---|---|---|---|---|
| Balanced | 87 °C | 84 °C | 89 °C | 91 °C |
| Silent / Custom | 75 °C | 72 °C | 77 °C | 79 °C |

GPU thresholds come from limits the device reports about itself, accepted only when they match a
signature validated against real hardware and paired with an anchor — the driver's current
thermal target — that has actually been observed on that part. **An unrecognised GPU, or an
unrecognised anchor, gets no thresholds and Dynamic Cooling refuses to start.** There is no
generic fallback — a wrong assumed threshold is worse than none, because it looks like
protection. Changing performance mode during a running session hands the fans back to firmware,
because the limits in force were derived for the mode the session qualified in; see
[known limitations](known-limitations.md) for the full mode-dependence detail.

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
  temperature is about 1.1–1.25 s, depending on session. Accepted and documented rather than
  fixed — see [known limitations](known-limitations.md).
- After an emergency handoff the runtime latches and will not resume on its own. Recovery is a
  deliberate restart, because an automatic retry after a thermal event is how you get a loop.
- Uninstalling preserves your settings.

## Licence

GPL-3.0. See [LICENSE](../LICENSE).

The licence is conveyed with the binaries, as GPL-3.0 sections 4 through 6 require: the MSI
installs `LICENSE.txt`, the portable zip carries it, and the installer's licence dialog presents
the GPL text rather than a summary.

---

## Fixed in this release

Four defects, all found by reading the shipped 0.1.1 interface against the hardware it was
describing rather than by a crash report:

- **Telemetry said "Live" while nothing was running.** The dashboard showed "Live — 1 s old"
  beside a runtime state of "Stopped", on a machine where firmware owned the fans. The sample
  genuinely was one second old — the interface polls while idle — but freshness and ownership
  are different claims. Idle states now read "Monitoring"; the age is still shown.
- **The compact panel had the same defect for the other states.** It special-cased Stopped
  alone, so Faulted and EmergencyHandoff still claimed live telemetry after cooling had gone
  back to firmware. Both surfaces now share one rule: only Running is live.
- **Scrollbars and checkboxes were drawn by Windows, not by the theme.** Neither had ever been
  templated, so a near-white system scrollbar appeared on every scrolling page and checkboxes
  rendered as white squares on near-black surfaces.
- **Diagnostics claimed GPU power was available when it never arrives.** The capability flag
  asked whether the driver had explicitly declined, not whether a reading existed.

GPU power and utilisation remain reported as unavailable, and
[known limitations](known-limitations.md) now explains why with measurements. Briefly:
utilisation is intermittent and tracks the discrete GPU's power state, and power is worse than
missing — the driver reports 593.5 W for a part rated near 150 W, to `nvidia-smi` as readily as
to BladeControl, mixed in among plausible values. A number that is occasionally absurd is not a
measurement, so it is not shown as one. Neither metric is used for any control decision.

---

## What changed since the 0.1.0 milestone

v0.1.0 was never tagged — it was a working milestone that reached feature-complete but stayed on
the `[Unreleased]` line in the changelog while several open questions were settled. v0.1.1 is the
first version actually tagged and shipped, carrying all of that resolution:

- **GPU thermal signature is mode-dependent, not fixed.** Dynamic Cooling no longer refuses
  outside a single hardcoded threshold set. Thermal control runs in whatever performance mode
  the machine is already in and qualifies against that mode's own limits (Balanced 87/89/92,
  Silent/Custom 75/77/80), anchored to the driver's current thermal target rather than to a
  value baked into the signature. Verified live, including a full session run in Silent + Manual
  with the machine never moved to Balanced.
- **Performance mode and fan ownership are orthogonal.** Taking or releasing fan control no
  longer forces a move to Balanced; Silent stays Silent, Custom stays Custom.
- **CPU and GPU performance levels are fully open**, except Overclock, which stays excluded so
  BladeControl cannot interfere with XTU tuning — see "What it does" above.
- **Licence settled as `GPL-3.0-or-later`**, declared once in `Directory.Build.props` so every
  built assembly carries it, with the full text conveyed per sections 4 through 6.
- **Cold-boot recovery validated on real hardware**: a genuine reboot, unaided delayed
  auto-start, qualification against the booted mode's own limits, a full session, and a clean
  stop restoring the booted state.
- **Visual identity added**: application icon, state-aware tray icon (idle/warning/emergency,
  reusing the same runtime-state-to-tone mapping the UI already used), and README/social-preview
  artwork.

Everything under "Not yet done" in [known limitations](known-limitations.md) remains genuinely
open; nothing above claims it as closed.
