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

**0. The GPU thermal signature — this one blocks Dynamic.** On the reference machine the
derivation now yields 87/89/92 C against a pinned signature of 75/77/80 C, so thermal ownership
is refused and Dynamic will not start. Fan control, performance modes and every fixed-target
path are unaffected.

**The cause is established.** The anchor is a function of the Razer performance mode —
Balanced gives 87, Silent and Custom give 75 — reproducible in both directions on the same GPU,
driver and specifications. Fan mode, temperature and elapsed time have no effect.

BladeControl performs thermal control exclusively in Balanced + Manual, so the pinned 75/77/80
is the signature of a mode the runtime never operates in. The original measurement was correct;
it was simply taken in Silent or Custom, and nvidia-smi agreed because it reports the same
driver-side target read in the same mode.

The constant was deliberately not edited, because changing 75 to 87 fixes this machine and
leaves the real defect in place. Two things are wrong independently of the number:

1. An exact-match allowlist pins a value the driver is entitled to change. The anchor is a
   thermal *target*, not a device identity.
2. Qualification reads it at start-preflight, which runs before the runtime has entered the
   mode it will operate in. Confirmed from the start path: `QualifyThermalOwnership()` is called
   before the state moves to `Starting` and before any mode transition, nothing requires the
   machine to already be in Balanced, and nothing re-reads the limits afterwards. A machine in
   Silent therefore qualifies against 75, switches to Balanced, and runs a ladder built on
   limits that no longer apply. The direction is conservative — it escalates about 12 C early,
   over-cooling rather than under-cooling, with firmware protection untouched — so it is a
   correctness defect and not a hazard. It is the mirror image of today's visible refusal: one
   bug with two faces, and an exact-match allowlist cannot tell them apart.

Today's refusal is the safe direction of that same bug. Options, recommended order:

- **Qualify in the operating mode.** Establish thermal limits after the runtime has taken
  Balanced + Manual ownership rather than before, and sanity-check the derived target against
  the legacy absolute thresholds (105/97/100) instead of a stored triple. This fixes the defect
  rather than the symptom. It touches the ownership gate, so it wants your sign-off.
- **Re-pin to 87/89/92** as an interim, documenting that the signature is mode-scoped. Restores
  Dynamic on this machine today; leaves both problems above.
- **Ship with Dynamic refusing**, which is honest and safe but makes the headline feature
  unavailable on the only validated machine.

Full experiment and raw data in [engineering-log.md](engineering-log.md).

The remaining four are the copyright holder's call, not an engineering task. None of them blocks
the build; all of them are easier to settle before a tag than after.

**1. `GPL-3.0-only` or `GPL-3.0-or-later`.** The repository says "GPL-3.0" throughout and
`LICENSE` is the plain GPL-3.0 text, which does not itself decide this. The FSF's own boilerplate
uses *or-later*, and *or-later* is what lets the project adopt a future GPL without tracking down
every contributor. *Only* gives you certainty about the exact terms forever. This has to be
settled before per-file headers are worth adding, because the header states the choice: 175
source files currently carry no licence header, and adding one is a mechanical pass once the
variant is chosen.

**2. Contributor licensing.** No CLA or DCO is in place. A DCO (`Signed-off-by`) is the lighter
option and is what the kernel and most GPL projects use; a CLA additionally lets the holder
relicense later. Doing nothing means inbound contributions arrive under GPL-3.0 by implication,
which is workable but leaves relicensing effectively impossible.

**3. Pre-release or full release.** Everything here was validated on exactly one machine, a
Razer Blade 16 (RZ09-0483). Qualification fails closed on anything else, so other hardware is
refused rather than mishandled — but "works on one laptop" is what a pre-release tag
communicates honestly and a `v0.1.0` tag does not.

**4. Cold-boot validation.** The service is installed `AUTO_START` and restarts on failure, but
a reboot into a working session has never been observed end to end. It needs a reboot of the
reference machine, which is the owner's to schedule.
