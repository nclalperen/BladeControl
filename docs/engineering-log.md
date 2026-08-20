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
