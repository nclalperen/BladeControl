# BladeControl engineering log

A chronological record of what was changed, why, what the evidence was, and what was ruled
out. The intent is that someone can audit the v0.1.0 release from this file plus the commit
history, without reconstructing a conversation.

Entries are newest-last. Each names the commit it describes where one exists.

Conventions used throughout:

- **Firmware-reported fan state** means Razer `0x0D81`. It is the commanded target echoed back.
  It is *not* a proven tachometer reading, and nothing in this project presents it as one.
- **Reference machine** means Razer Blade 16 (RZ09-0483), Intel i9-13950HX, NVIDIA GeForce
  RTX 4090 Laptop GPU. Every live result below is from that one machine unless stated.
- A claim marked **unproven** stayed unproven; it was not quietly promoted later.

---

## Validated thermal policy baseline

Do not change these without live re-validation.

### CPU

| Condition | Action |
|---|---|
| ≥ 90 °C | critical cooling, validated maximum fan target |
| ≤ 85 °C × 3 samples | release the critical override |
| ≥ 95 °C × 3 samples | emergency firmware-Auto handoff |
| ≥ 100 °C × 1 sample | immediate handoff |

### GPU — validated thermal signature only

`NVIDIA GeForce RTX 4090 Laptop GPU`, derived limits 75 / 77 / 80 °C.

| Condition | Action |
|---|---|
| ≥ 75 °C (max operating) | critical cooling, validated maximum fan target |
| ≤ 72 °C × 3 samples | release the critical override |
| ≥ 77 °C (hardware slowdown) × 3 samples | emergency firmware-Auto handoff |
| ≥ 79 °C (1 °C below hardware shutdown) × 1 | immediate handoff |

An unrecognised GPU, or one whose derived limits do not reproduce its validated signature,
yields no limits at all: Dynamic refuses to start and zero SETs are sent. There is no generic
Ada fallback and no return of the old fixed 80 °C threshold.

---

## 2026-08-20 — Scheduler and actuator I/O cost

**Commit:** `68e4a39` — *Scope fan-target verification and correct scheduler metrics*

### Evidence that opened this

A live Dynamic session on the reference machine reported:

```
Requested period    500 ms
Completed cycles    791
Overruns            136  (~17.2%)
Max overrun         1.22 s
Skipped deadlines   0
Scheduler health    Degraded
```

with a representative target-change cycle showing sixteen HID exchanges, and a decaying
catch-up tail afterwards: 376.9 → 254.9 → 129.9 → 4.7 ms.

### Hypotheses considered

| Hypothesis | Outcome |
|---|---|
| Excessive synchronous Razer verification | **Confirmed** — 10 of 16 exchanges fed no predicate |
| Overrun counter conflates cause with recovery | **Confirmed** by reading the scheduler |
| Watchdog duplication on write cycles | **Confirmed** — a second 0x0D82 pair microseconds after one already taken |
| Fixed sleeps on the control path | **Rejected** — `IFanObservationDelay` is only used by `ObserveFixedFanTargets`, which the thermal path never calls |
| Telemetry performing HID reads | **Rejected** — the fast path (`GetControlSample`) is CPU + GPU only |
| Scheduler drift | **Rejected** — deadlines advance absolutely and self-correct |
| Telemetry provider latency | **Open** — not addressed by this commit; see below |

### What changed

Fan-target write path, before and after:

| | Before | After |
|---|---|---|
| Precondition | `0x0D82 ×2`, `0x0D81 ×2`, `0x0D87 ×2` | `0x0D82 ×2` |
| Write | `0x0D01 ×2` | `0x0D01 ×2` |
| Verification | `0x0D82 ×2`, `0x0D81 ×2`, `0x0D87 ×2` | `0x0D81 ×2`, `0x0D82 ×2` |
| Watchdog (when due) | `0x0D82 ×2` | coalesced into the verification pair |
| **Total** | **16** | **8** |

`0x0D87` is no longer read on this path at all: CPU and GPU performance levels are restoration
data, captured once at start.

Verification reads fan state *before* ownership deliberately, so the `0x0D82` pair is last and
the observation the operation returns is the freshest thing in it.

### Watchdog coalescing contract

The post-write ownership observation carries a `Stopwatch` timestamp taken immediately after
its **second `0x0D82` response**, not when the operation returned — an observation that
reported itself fresher than it is could let a stale reading answer a deadline.

It may satisfy a due watchdog only when its **measured** age ≤ one control period. Ownership is
never inferred from the write having succeeded, the pre-write observation is never offered, and
the observation is cleared at the top of every cycle so nothing carries over. Anything that
does not qualify falls through to a normal read. Due points still advance in absolute steps.

### Scheduler metric semantics

The previous single `OverrunCount` incremented both when a cycle's own body exceeded the period
and when a cycle merely started late because an earlier one had. One slow cycle therefore
reported as several faults — which is how 791 cycles came to show 136 "overruns" from far fewer
events.

| Metric | Meaning |
|---|---|
| `SlowCycleCount` | the cycle's own body exceeded the period — a cause |
| `CatchUpCycleCount` | the cycle began late because an earlier one overran — a consequence |
| `MissedDeadlinePeriods` | whole period boundaries lost while running late |
| `LatestStartToStart` | most recent interval only (was misleadingly `ActualStartToStart`) |
| `LatestCycleExecutionDuration`, `LatestDeadlineLateness` | most recent cycle only |
| `MaximumCycleExecutionDuration`, `MaximumDeadlineLateness` | retained maxima |
| `SkippedDeadlines` | **defined as always zero** — the loop provably never skips an iteration |

`SkippedDeadlines` was previously hardcoded `0` and never computed, which made it a lie rather
than merely unhelpful. It is retained as an explicitly documented zero because a consumer
reading a missing field as "unknown" would be worse.

Bounded 256-sample rolling statistics (latest / max / p95 / p99) were added for cycle
execution, telemetry acquisition and actuator duration, with CPU and GPU provider time measured
separately. Recording is O(1) and allocation-free; percentiles are computed only when
diagnostics reads them, never on the 500 ms path.

### Known limitation carried forward

Acquisition and actuation still share one thread and one operation gate. A critical thermal
sample can still wait behind an in-flight fan write — the window is now roughly eight HID
exchanges rather than sixteen, not zero. Whether that matters is a question for live
measurement, not for assumption.

**Explicitly not concluded:** that LibreHardwareMonitor always costs ~380 ms. A later
diagnostic sample from the installed RC showed telemetry acquisition varying between ~150–200 ms
in good periods and ~250–400 ms in slower ones. Any threading redesign must rest on
distribution data from the new build, not on a single earlier snapshot.

### Tests

735 passing at this commit.

New: `SchedulerMetricSemanticsTests` (13), `WatchdogCoalescingTests` (6), and five additions to
`FanControlTests` asserting the exact ordered exchange sequence and the absence of `0x0D87`.

Two existing assertions were changed to match intentionally changed measurements, and are named
here rather than left buried:

- `SixHundredMillisecondsWorkNeverOverlapsAndRecordsOverruns` expected `MaximumOverrun == 300`
  (distance past the next deadline); it now expects `MaximumCycleExecutionDuration == 600`, the
  body's own time.
- `RunsWithoutWritesAlwaysReadTheWatchdog` originally asserted zero writes; a settling cycle
  does write once, so it now asserts `reads > writes`, which is the claim the test is about.

---

## 2026-08-20 — Diagnostics could not reach the installed runtime

**Commit:** `db45574` — *Let diagnostics reach a LocalSystem runtime and report absences honestly*

Found while trying to read live metrics off the installed service, not by inspection.

### The CLI could not connect at all

`NamedPipeRuntimeIpcClient` still passed `PipeOptions.CurrentUserOnly`, a leftover from when
the runtime was user-hosted. Once the runtime became a LocalSystem service, .NET's
`ValidateRemotePipeUser` rejected every connection with `UnauthorizedAccessException` before a
request was sent. The GUI client had already been corrected for this; the CLI had not, so the
defect was invisible to anyone using the GUI.

Fixed the same way: connect without `CurrentUserOnly`, then verify explicitly that the pipe was
published by a privileged account — which also defeats a pipe squatted by an unprivileged
process.

### The new component timings never crossed IPC

Telemetry acquisition, actuator duration and the watchdog coalescing count were added to the
in-process `RuntimeStatus` record but not to `RuntimeStatusDto`. No external consumer could see
the measurements the previous batch existed to produce. Added to the DTO and its mapping.

### Absence was rendered as measurement

A runtime older than these fields sends none of them, and a plain `long` deserialises to zero.
The CLI therefore printed `Slow cycles 0` for a runtime that had just reported 281 overruns.
The block is now withheld with an explicit note. The nullable statistics object is the marker
that distinguishes "none" from "not sent"; a `long` cannot. A missing block also no longer
throws — a diagnostic tool that crashes on a field the other end did not send is useless during
exactly the upgrade window where it is most needed.

### Live baseline captured from the pre-batch RC

Read from the installed (older) service before any upgrade:

```
State                  Running
Session                ca8e15e8-7d81-4098-9ad6-afdb8af82a73, Thermal/default
Zone 1 / Zone 2        Balanced + Manual, agreeing
Health                 Healthy, no LastFailure
Completed cycles       5879
Old-model overruns     281  (~4.8%)
Last acquisition       348.8 ms and 164.5 ms, two samples ~2 min apart
```

Those two acquisition samples are the important part: **telemetry acquisition is variable, not
a constant ~380 ms.** Any threading redesign must rest on a distribution from the new build, not
on a single snapshot. This is recorded because an earlier analysis leaned on one ~390 ms figure
and would have justified a redesign that the data does not support.

---

## 2026-08-20 — Physical fan RPM investigation

**Question:** is there any trustworthy source of *actual* fan speed, as opposed to Razer
`0x0D81`, which returns the commanded target echoed back?

Searched in the agreed order. All read-only; nothing was written to any device.

| Source | Result | Conclusion |
|---|---|---|
| LibreHardwareMonitor `Fan` / `Control` / `Flow` sensors | **0 sensors** across motherboard (SO690), CPU, both GPUs and battery | No tachometer exposed |
| NVML / `nvidia-smi --query-gpu=fan.speed` | **`[N/A]`** | The GPU fan is EC-controlled, not driver-controlled; NVML has no visibility |
| WMI `Win32_Fan` | Two `Cooling Device` instances, `Status OK`, but `DesiredSpeed` and `VariableSpeed` both **empty** | Presence only, no speed |
| WMI `MSAcpi_ThermalZoneTemperature` | Access denied (non-elevated) | Not a fan source regardless |
| Razer HID `0x0D81` | Returns the commanded target | **Unproven** as a tachometer; unchanged |

**Caveat closed.** The LibreHardwareMonitor probe was first run non-elevated and later repeated
with full Administrator rights. Both runs enumerated the same hardware — motherboard SO690, the
i9-13950HX, both GPUs, the battery — and both found **zero** Fan, Control or Flow sensors. The
absence is a property of the hardware, not of the privilege level.

**Outcome:** physical fan RPM is **not available** on this hardware through any read-only
source examined. The product must continue to present `0x0D81` as *firmware-reported fan state*
and leave actual RPM unavailable, which is what it already does. This investigation confirms
the existing design rather than prompting a change.

The alternative — presenting `0x0D81` as RPM because it is numerically plausible — is
specifically rejected. It equals the commanded target by construction, so it would agree with
itself no matter what the fans were physically doing, including not spinning at all.

---

## 2026-08-20 — Blocked on elevation

The session account is **not a member of Administrators**, so elevation cannot be obtained even
via a UAC prompt. The following cannot proceed autonomously:

- stopping or starting `BladeControl.Runtime`
- installing or upgrading the MSI
- any elevated re-probe (`MSAcpi_ThermalZoneTemperature`, elevated LibreHardwareMonitor EC scan)

The installed RC predates both commits above and is still running a healthy Dynamic session, so
nothing is in an unsafe state. Repository work continued; live validation of the new build is
outstanding.


---

## 2026-08-20 — Elevated live validation

Session elevated to `BUILTIN\Administrators`, unblocking service control, the installer and
elevated probes.

### Service stop path — verified on hardware

The first live confirmation that shutdown hands the fans back rather than abandoning them in
Manual:

```
Stop-Service         completed in 1052 ms, no hang
Fan mode             Auto      firmware owns cooling
Performance          Custom    captured original state restored
Reported fan 1 / 2   4500 RPM  firmware Auto's own value, not a stale Manual target
```

### Event log

The only `BladeControl Runtime` entries are Warnings reading *"Transient IPC connection fault;
continuing to serve the channel."* No Error-level events, no SCM failure, no service crash.

Those warnings correlate exactly with CLI processes that crashed mid-exchange during this
session, which makes them live evidence that the accept-loop resilience added earlier works: a
client vanishing mid-request no longer costs the runtime its hardware.

### The WER entries were the CLI, not the service

`BladeControl.Cli.exe` filed two Windows Error Reporting crashes. Both causes were already
fixed, but the failure mode was closed separately in `2d7b842`: a diagnostic tool that appears
in the event log as a faulting application, immediately beside the events it exists to help
interpret, obstructs the diagnosis. `Main` now reports and exits non-zero.

### Elevated telemetry doctor — fully qualified

```
Razer HID                    available
NVML                         available
GPU thermal limits           75 / 77 / 80 C, validated thermal signature
PawnIO                       available 2.2.0.0, Running, Authenticode Valid
PawnIO CPU provenance safety safe
CPU Package temp             available
Thermal ownership qualification (authoritative)
  Verdict            QUALIFIED
```

Two open items close here:

1. **CPU Package temperature was unavailable in an earlier non-elevated harness.** That was an
   elevation artefact, not a product defect — it reads correctly with Administrator rights.
2. **The unexplained "GPU thermal limits unavailable" from an earlier RC's doctor run.** Under
   elevation on the current build, discovery succeeds and reports the validated 75/77/80
   signature. The production path is confirmed working under the conditions the user actually
   runs it. The earlier report is not reproducible on this build and was never reproduced in
   ten consecutive non-elevated attempts either; it is recorded as unexplained rather than
   quietly dropped, and the doctor now prints the discovery reason inline so a recurrence
   explains itself.

### Old-RC final baseline before upgrade

```
Completed cycles     6895
Old-model overruns   309  (~4.5%)
Last acquisition     223.4 ms
```

Three acquisition samples across the session: 348.8, 164.5, 223.4 ms. Variable, as stated.
