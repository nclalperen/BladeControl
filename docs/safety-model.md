# BladeControl safety model

What BladeControl will and will not do to your machine's cooling, and why each rule exists.

This document is normative. If the code and this file disagree, that is a bug in one of them.

---

## The one-sentence version

BladeControl borrows fan control from firmware, holds it only while it can continuously prove
it still has it, and gives it back the moment anything is uncertain.

---

## What the software is actually controlling

BladeControl asks the Razer firmware to switch both thermal zones from **Auto** (firmware
decides fan speed) to **Manual** (BladeControl sets a target), then writes fan targets on a
500 ms cadence from a temperature curve.

It does **not** modify firmware, disable any thermal protection, or reach below the documented
Razer HID command set. The CPU's own throttling and the GPU's hardware slowdown and shutdown
remain fully in force at all times and are not BladeControl's to defeat — they are the backstop
that makes everything below merely the *first* line of defence rather than the only one.

---

## Taking ownership

A session may only begin if every one of these holds. Any failure refuses the start, leaves the
runtime `Stopped`, and sends **zero** writes to the hardware.

| Prerequisite | Why |
|---|---|
| CPU provider provenance is safe | PawnIO's driver is Authenticode-verified before its readings are trusted for a control decision |
| CPU package temperature present, plausible, fresh | A control loop without an authoritative CPU reading is guessing |
| GPU temperature present, plausible, fresh, from NVML | Same, and it must come from the authoritative provider rather than any fallback |
| Exactly one NVIDIA GPU selected deterministically | An ambiguous selection means the limits might belong to a different device |
| Razer HID management interface available | Without it there is no way to hand control back |
| **GPU thermal limits established** | See *GPU limits* below — there is no default threshold |

### Restoration state must be stable before ownership

The state a session promises to put back is read until the machine reports the same one **twice
consecutively**, symmetric across both zones, with at most three reads. No sleeps, no retry
loop.

A capture is six sequential HID GETs with no atomic firmware snapshot behind it, so it can
observe a state the machine was only passing through. Two differing captures establish that the
restoration state was not persistent across the read window. They cannot distinguish a brief
firmware transition from a read sequence that straddled one, and nothing in the code claims
they can — it refuses either way, because either way the captured state is not something to
promise to restore.

The fingerprint compared is exactly what restoration writes back: both zones' performance modes
and the CPU and GPU levels. Fan mode is deliberately excluded — it is never restored, so it must
not be able to destabilise an otherwise identical pair.

### The ownership gate is a fresh read, and it is last

Immediately before the first write, and after everything else has passed, BladeControl reads
both zones' performance and fan mode (two GET `0x0D82` exchanges). Both zones must agree and
both must report **Auto**.

That same read also confirms the stabilised performance modes are still current, closing the
window between adopting a restoration state and taking ownership without costing another read.

Nothing capable of refusing the start sits between that read and the first write.

---

## GPU limits: no threshold is ever assumed

GPU thermal thresholds are derived from what the device reports about itself — NVML T.Limit
specifications anchored by the live margin — and are accepted only when they match a **validated
thermal signature**: an exact GPU identity paired with the exact limits that identity was
observed to produce on real hardware.

A GPU with no validated signature, or one whose derived limits no longer reproduce it, yields
**no limits at all**. Dynamic refuses to start and sends zero writes.

There is deliberately no generic fallback. BladeControl previously handed off at a hard-coded
80 °C; on the reference RTX 4090 Laptop GPU that turned out to be the temperature at which the
hardware *shuts itself down*. A wrong assumed threshold is worse than no threshold, because it
looks like protection.

---

## Holding ownership

### The graded response

Heat is not a single cliff. Each sensor has its own ladder, and both are advanced every cycle
so neither can be starved by the other reaching a threshold first.

| CPU | Action |
|---|---|
| ≥ 90 °C | maximum validated fan target; keep control |
| ≤ 85 °C × 3 samples | release the override |
| ≥ 95 °C × 3 samples | hand back to firmware Auto |
| ≥ 100 °C × 1 sample | hand back immediately |

| GPU (reference signature 75 / 77 / 80 °C) | Action |
|---|---|
| ≥ 75 °C (max operating) | maximum validated fan target; keep control |
| ≤ 72 °C × 3 samples | release the override |
| ≥ 77 °C (hardware slowdown) × 3 samples | hand back to firmware Auto |
| ≥ 79 °C (1 °C below hardware shutdown) × 1 | hand back immediately |

The 1 °C pre-shutdown margin and the 3 °C recovery hysteresis are **BladeControl policy, not
device specifications**. NVML reports the shutdown temperature; it does not report where we
choose to act relative to it.

Maximum cooling is held while *either* sensor is critical, and released only when **both** have
independently recovered. One sensor cooling down never withdraws cooling the other still needs.

The critical override bypasses the curve, the hysteresis, the downward-ramp qualification and
the write-coalescing interval. Those exist to keep ordinary fan behaviour calm; none of them may
delay an upward response to a hot part.

### The watchdog

Every five seconds BladeControl re-reads both zones and confirms it still holds Balanced +
Manual. If firmware or another application has taken the fans back, the session ends.

When a fan write has *just* read that same state, its observation may answer the deadline
instead of issuing a second identical pair — but only if its measured age is within one control
period. Ownership is never inferred from a write having succeeded, the pre-write observation is
never reused, and the observation is discarded every cycle.

### Every write is verified

A fan-target change issues exactly eight HID exchanges:

```
0x0D82 ×2   ownership still Balanced + Manual, zones agree
0x0D01 ×2   the write, echo-validated
0x0D81 ×2   firmware-reported fan state equals the commanded target
0x0D82 ×2   ownership still held, zones still agree
```

If any check fails, the fans go back to firmware Auto immediately.

---

## Giving ownership back

Whether a session ends normally, by emergency, or because the service is stopping, the order is
always the same:

1. establish firmware **Auto** — cooling belongs to firmware again
2. restore the captured performance state
3. only then report the session finished

The captured *fan mode* is never restored. If a session began while the machine happened to be
in Manual, it does not put it back into Manual — it leaves firmware in control.

### Emergency handoff is success, not failure

A handoff that reached verified firmware Auto is reported as `EmergencyHandoff`, distinct from
`Faulted`. The protection working is not the same event as the protection being unable to run,
and conflating them made a correct safety action look like a broken runtime.

The runtime latches in that state and will not resume on its own. Recovery is a deliberate human
action.

---

## What the software will not claim

- **`0x0D81` is not a tachometer.** It returns the commanded target echoed back. Actual physical
  fan RPM is not available on the validated hardware through any read-only source examined, and
  the product reports it as unavailable rather than presenting a number that would agree with
  itself whatever the fans were doing.
- **Only a running session reports current readings.** Stopped, Faulted and EmergencyHandoff all
  present the last thing observed, labelled as history.
- **Absence is not zero.** A metric a runtime did not report is shown as not reported.

---

## Known limitations

- **Acquisition and actuation share a thread.** A newly observed critical temperature can wait
  behind an in-flight fan write. Measured worst case on the reference machine is roughly one
  control period; for the three-sample emergency ladders this is comfortable, and for the
  single-sample immediate handoffs it means up to about a second of detection delay. Firmware
  slowdown and shutdown remain the backstop.
- **One validated machine.** See the hardware scope in the README. Everything above is validated
  on a single Razer Blade 16 (RZ09-0483).
