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

---

## 2026-08-20 — New RC deployed and measured

**Deployed:** `1850783`, MSI `cd8a108c497b8b3c051bed217c36dfb1b65bb6ab4ea79ebba86837d3da8c4773`,
update-in-place, exit code 0, service Running/Automatic, all five runtime assemblies verified
byte-identical to the publish output.

### The 8-exchange path, confirmed on hardware

From a verbose protocol capture, `0x0D01` writes appear at three target changes. The first
`0x0D87` in the entire log appears well after the last of them, in this context:

```
0x0D82, 0x0D82, 0x0D87, 0x0D87   ← tail of a six-GET ReadCompleteStatus (diagnostics)
```

**Fan-target changes issue no `0x0D87`.** Remaining `0x0D87` traffic is session-start
stabilisation and periodic diagnostic snapshots — separate paths, correctly unchanged.

`GetDiagnosticSnapshot` takes the same operation gate as the control cycle, so HID access is
serialised. There is no parallel transport access.

### Rejected hypothesis: verbose logging inflates the control cycle

An early reading showed actuator 863 ms and cycle 1244 ms under `--verbose`, against 236 ms and
525 ms without it. The obvious inference — that per-exchange event emission was distorting the
loop — was **wrong**.

Those samples came from the cycle in which an emergency handoff occurred. That cycle
deliberately performs `ReturnToBalancedAuto` and `RestorePerformance`, both full HID sequences,
and it is the last cycle of the session. Nothing to do with verbosity. Recorded because the
wrong conclusion was one step away and would have sent the next batch chasing the logger.

### CPU 100 °C immediate handoff — live-validated

A session ended with:

```
Emergency firmware handoff required: CPU Package temperature reached 100.0 C,
at or above the 100 C immediate limit.
```

Provoked naturally by concurrent builds loading the CPU, not manufactured. The single-sample
immediate handoff fired as specified, the service stayed Running with no errors in the event
log, and the runtime latched in `EmergencyHandoff` awaiting a deliberate restart.

### Defect found by that handoff

While latched in `EmergencyHandoff`, the status report announced:

```
Current watchdog observation
  Zone 1   Balanced + Manual
```

Firmware Auto owned the fans at that moment. The historical labelling tested only for `Stopped`,
so `EmergencyHandoff` and `Faulted` kept a "Current" heading over stale data — claiming
BladeControl still owned the fans at exactly the moment it had handed them back. Fixed in
`f99d8bc` for both CLI and GUI, with regression tests across all four states.

### Scheduler measurement — 252 cycles, corrected metrics

| | latest | p95 | p99 | max |
|---|---|---|---|---|
| Telemetry acquisition | 381.7 | 392.7 | 402.6 | 423.1 ms |
| Actuator (8-exchange write) | 0.0 | 235.0 | 244.5 | 244.9 ms |
| Whole cycle | 381.8 | 613.8 | 624.2 | 652.5 ms |

Slow cycles 13, catch-up cycles 25, **missed periods 0**, maximum lateness 153.6 ms, watchdog
coalescing fired 3 times.

### Decision gate: case B

Acquisition alone consumes ~390 ms of the 500 ms period, so **any cycle that also writes
overruns**, by roughly 125 ms. Halving the write from sixteen exchanges to eight halved that
cost without eliminating it: ~30 ms per HID exchange × 8 is ~244 ms, and eight is the minimum
that can still be verified.

Not case C. The schedule self-corrects, no period was ever lost, and worst-case detection delay
for a newly critical temperature is about 1.1 s — comfortable against the three-sample ladders,
and about a second against the single-sample immediate handoffs, with firmware slowdown and
shutdown as the backstop.

**Accepted and documented for v0.1.0 rather than fixed.** Separating acquisition from actuation
introduces sole-HID-ownership, sample-freshness and preemption invariants that deserve their own
design pass rather than being attached to a latency patch.

### Normal stop — validated

`runtime stop-thermal` → firmware **Auto**, performance **Custom** restored, verified by reading
firmware directly with the service stopped. Service restarted cleanly afterwards.

### GPU ladders not naturally exercised

GPU stayed at 48 °C under this workload, well below the 75 °C critical threshold. Per the
standing instruction, no attempt was made to manufacture GPU heat. Those paths remain covered by
unit tests only.

---

## 2026-08-20 — UI truthfulness batch and installer lifecycle

**Commits:** `37cd69e`, `af38bd1`, `55db3e4`, `42244e6`, `87cac22`, `1f3a1e6`

### Items that turned out to be already correct

Checked rather than assumed, and left alone:

- Unavailable GPU levels were already `IsEnabled`-bound to availability, dimmed to 0.42 opacity,
  cursor-changed, and tooltipped with the blocked reason.
- CPU and GPU Custom rows were already separate and labelled.
- The compact panel already had a single segmented Auto / Fixed / Dynamic control with
  mode-specific detail beneath it.

### The duplicate rejection banner had a root cause, not a presentation quirk

`RejectStart` wrote its reason into the same field as `Fault`, so the dashboard rendered
"Runtime failure: …" beside the operation's own rejection message. One refusal, reported twice,
with a safe refusal called a failure — while the runtime was `Stopped`, healthy, and had sent no
write.

The runtime draws that distinction deliberately; writing both into one field erased it a layer
up. The reason now has its own field, and the dashboard raises no alert for a refusal because
the refused operation already reports it.

### ComboBox popup

The theme styled `ComboBox` but not `ComboBoxItem`, so the dropdown inherited system colours:
near-black text on a near-black list. Items present, selectable, effectively invisible. Styling
the control you click is the easy half; the popup is where the text has to be read.

### Scheduler health, no-session state, observation ages

Health now derives from slow cycles inside the rolling window rather than cumulative counts, so
one slow cycle no longer reads "Degraded" for the life of the session; the lifetime totals are
reported alongside. A runtime that has never run a session says so instead of showing a table of
zeros under a health verdict. Watchdog observations carry the time they were taken and their age
is displayed, rendered coarsely because false precision on "how stale is this" would be its own
small dishonesty.

### The runtime now reports its own build

Establishing that an installed runtime predated a commit previously meant hashing five
assemblies against a publish directory. The build stamps the short source commit into
`InformationalVersion` and the runtime carries it across IPC. Confirmed live: after deploying
`1f3a1e6`, `runtime status` reported `Build 0.1.0+1f3a1e643bca`.

### Installer lifecycle — validated end to end

| Step | Result |
|---|---|
| Upgrade in place | exit 0, service Running/Automatic, build identifier matched the deployed commit |
| Uninstall | exit 0, service **removed**, install directory **removed** |
| Firmware state after uninstall | **Fan mode Auto**, performance Custom — fans not left in Manual |
| User settings after uninstall | **preserved** at `%LOCALAPPDATA%\BladeControl\ui-settings.json` |
| Reinstall | exit 0, service Running/Automatic |
| Settings after reinstall | **byte-identical**, honoured by the fresh install |
| Event log across the whole cycle | no Error-level BladeControl entries |

Settings surviving uninstall is deliberate and correct: uninstalling an application should not
destroy the user's preferences.

### Dashboard vocabulary

"Live — provider-only sample" was engineering vocabulary on a user-facing surface. The dashboard
now answers whether the reading is live and how old it is; the acquisition provenance moved to
Diagnostics rather than being deleted.

### Noted, not fixed

`RuntimeStatusDto` is a positional record with twenty-three parameters. Four separate field
additions in this session each broke the same three test fixtures, silently and only at compile
time. It works, but the shape invites exactly the kind of positional mistake that would be
invisible if two adjacent parameters shared a type. Worth a named-argument or builder pass.

## Crash recovery, and two defects it exposed

The orphaned-Manual recovery path had never been run against a real crash. It is the only thing
between a killed process and fans held at a fixed speed indefinitely, so it was tested directly:
a session was started, the service process was killed with `Stop-Process -Force`, and the
machine was left to the Service Control Manager.

It worked. The SCM restarted the service after ~25 s, and the firmware came back **Balanced +
Auto**. That is the designed behaviour, observed for the first time.

Two defects surfaced in what the runtime *said* about it.

### The recovery reported the state it had just replaced

`InitializeHost` recorded the startup read — Balanced + Manual — as the watchdog observation,
then recovered to Auto and discarded `recovery.FinalState`, a readback taken a few exchanges
later. Every diagnostic afterwards reported Manual. A machine the runtime had already fixed was
described as still being held in Manual, which reads as the recovery never having run. The
fresher observation existed the whole time and was being thrown away. It is now adopted —
whatever the outcome, since a failed recovery's final state is equally the most recent thing
known about the hardware, and in that case it correctly reports Manual.

### The one moment "Last failure" mattered was the one moment it was hidden

`Total events`, `Last failure` and `Emergency status` were rendered from inside the scheduler
section, behind its two early returns. Those returns are right in themselves — a table of zeros
under a "Healthy" heading is absence dressed as measurement — but they were ending the whole
report, not just their own block.

A process that crashed, was restarted, and *failed* to recover orphaned Manual mode has by
definition run no session. It is Faulted, `LastFailureReason` names the failed recovery, and
`runtime status --verbose` suppressed exactly that line. Confirmed live: the first crash test's
verbose output ended at "No session has run since the runtime started."

The scheduler block is now its own method, so its early returns end the scheduler block and
nothing else. Scheduler history and runtime diagnostics are different subjects; the absence of
the first says nothing about the second.

This is the same shape as everything else in this log: a condition modelled as the wrong kind of
thing. Runtime-lifetime diagnostics were nested inside per-session scheduler rendering, so "no
session ran" was allowed to mean "nothing to report about the runtime."

### What was not done

The recovery was validated live once, on an idle machine, and the reporting fixes are pinned by
unit tests rather than by a second crash. Re-killing the service to re-read a corrected string
is not worth another uncontrolled window; the fixes are behavioural and covered.

## Caption prose was being clipped

None of the shared text styles set `TextWrapping`, and `CaptionTextStyle` is where every
surface puts its qualifying prose — what a reading means, why a control is unavailable, which
values are commands rather than measurements. Six blocks of explanatory text were clipping at
the panel edge, including the sentence on the Fans page saying fan values are commands and not
tachometer readings.

A clipped qualifier is worse than no qualifier: "Values are commands, not physical" reads as a
stronger claim than the full sentence, and the half that got cut is the half doing the work.
Fixed at the style rather than per call site, so the four caption-styled blocks and the two
performance summaries are covered together. Eyebrows and metrics are deliberately excluded —
short labels and single values are broken by wrapping, not saved by it.

## The licence did not ship with the binaries

Choosing GPL-3.0 and committing `LICENSE` is not the same as conveying it. Three things were
still wrong, and none of them would have failed a build:

- **`installer/License.rtf` predated the decision.** It told every installing user the project
  "has not yet selected a final open-source licence." It would have shipped that claim to every
  machine while `LICENSE` sat in the same install folder saying GPL-3.0 — the installer
  contradicting the product on the one point people consult an installer about.
- **The MSI shipped `THIRD-PARTY-NOTICES.md` and not `LICENSE`**, under a component comment
  reading "Licensing and attribution travel with the product." Every third party's licence
  travelled with the product; BladeControl's own did not.
- **The portable zip had the same omission.**

GPL-3.0 sections 4 through 6 require the licence text to accompany the object code it covers.
`License.rtf` is now generated from `LICENSE` itself, with a preamble that states the GPL grants
rights rather than restricting use and that acceptance is not a condition of running the
program — which is what the FSF actually says, and worth saying on a dialog whose button reads
"I accept".

Verified at the binary level: the built MSI's `File` table contains `LICENSE.txt`.

`CHANGELOG.md` carried two claims that had gone stale the same way — that no licence had been
applied, and that the installed product had never been exercised on hardware. Both were true
when written and false by the time they were read. Corrected rather than deleted.

## Portable package, validated as far as it honestly can be

`pack.ps1` produced all three artifacts. The portable zip now carries `LICENSE.txt` alongside
`THIRD-PARTY-NOTICES.md` and `README-PORTABLE.md`, and the MSI's `File` table was checked
directly rather than inferred: `LICENSE.txt` is present among its 484 files.

Both portable executables were exercised from the extracted tree:

| Check | Result |
|---|---|
| `BladeControl.UI.exe` | launched and stayed up - the WPF payload is complete |
| `BladeControl.Service.exe` with no arguments | refused with usage text, as designed |
| `BladeControl.Service.exe console` | opened both hardware sessions, then **refused at the ownership gate** |

The refusal is the interesting result twice over. It confirms the gate prevents a second
controller, and it confirms the portable payload resolves HidSharp and LibreHardwareMonitor,
since the host reached hardware initialisation before being turned away.

It also exposed the ordering: hardware is opened before ownership is established. Recorded in
`docs/known-limitations.md` and deliberately not fixed in this cycle - the lease is acquired
inside `InitializeHost`, which is also the crash-recovery path, and reordering it is not worth
the risk for a change with no safety consequence.

What this does **not** show: the zip was unpacked on the machine that already has the MSI
installed. A dependency the installed product happens to satisfy would not have surfaced. The
limitation says so rather than claiming a clean-machine result that was never obtained.

## Six documentation claims that had gone stale

`installer/License.rtf` turned out not to be the only document asserting a state that had since
changed, so the docs were swept for pending and negative claims — "has not been", "not yet",
"never been", "left to". Six were true when written and false by the time anyone would read
them:

| Document | Claimed | Actually |
|---|---|---|
| `install-test-checklist.md` | "**Nothing here has been executed**" | all of it had been, on the reference machine |
| `THIRD-PARTY-NOTICES.md` | licence "Not yet applied" | GPL-3.0, applied and conveyed |
| `README.md` | runtime exposes no build identifier | it does, confirmed live |
| `gui-v0.1.md` | five open backend limits | two closed, one half closed |
| `gui-backend-needs.md` §4 | no version/build identity | `RuntimeStatusDto.RuntimeBuild` |
| `gui-backend-needs.md` §3 | pipe "restricted to the creating user" | an explicit privilege boundary |

The last one was the worst of them. It described the console-hosted runtime, where server and
client were the same account, and became wrong the moment the runtime became a LocalSystem
service — `CurrentUserOnly` would assert the server runs as the connecting user, which is
precisely what stopped being true. A reader trusting that sentence would have concluded the pipe
was safe for a reason that no longer applied.

My own first pass at the IPC correction was itself overstated: I wrote that the contract and
endpoint had moved to `BladeControl.Ipc`. Only the endpoint and pipe security did. The DTOs are
still in `BladeControl.Runtime`, which the GUI still references. Caught by checking the project
file instead of trusting the tidier claim, and corrected before it was committed.

Documentation goes stale the same way code does, and nothing compiles it. The negative claims
are the ones to re-read, because they are the ones that quietly become false by being fixed.

## The GPU thermal anchor moved, and the gate caught it

Deploying the release MSI and starting a session produced a refusal:

> Fresh thermal ownership qualification failed: GPU thermal limits could not be established
> ... derived thermal limits (87/89/92 C) do not match its validated signature (75/77/80 C).
> The device is no longer behaving as it did when its T.Limit data was interpreted. No SET
> was sent.

This is the first reproduction of the "GPU thermal limits unavailable" behaviour seen on an
older release candidate and never reproduced since. It is worth being precise about what it
shows, because the safety design worked and the underlying assumption did not.

### The raw numbers

The read-only probe, correlated with `nvidia-smi`:

| Quantity | Value |
|---|---|
| Core temperature | 48 C |
| `nvmlDeviceGetMarginTemperature` | **39 C** |
| T.Limit specifications (193 / 194 / 196) | -5 / -2 / 0 C |
| Derived anchor (`temperature + margin`) | **87 C** |
| Derived limits (`anchor - specification`) | 87 / 89 / 92 C |
| Legacy absolute thresholds | GPU_MAX 105, SLOWDOWN 97, SHUTDOWN 100 C |

The pinned signature of 75/77/80 requires an anchor of 75, which at 48 C means a margin of 27.
The same machine now reports 39. Nothing else changed: same GPU UUID, same driver (610.88),
same specifications.

### The anchor is stable within a session and not across them

Ten samples over ninety seconds at idle:

```
temp 47  margin 40  anchor 87   P8, 210 MHz, 10.3 W   (x10, no variation)
```

Margin tracks temperature exactly - 47+40 and 48+39 both give 87 - so the derivation's
moment-to-moment invariant holds. What moved is the anchor itself, between one session and
another.

### What that means

`temperature + margin` is **not a device constant**. It is the thermal target the driver is
currently enforcing, and that target legitimately varies with power and performance policy.
The derivation was built on the assumption that it is fixed, and it is not.

This has a direct consequence for the validated-signature allowlist: pinning an exact triple
matches a value that is allowed to change, so Dynamic is refused whenever the current target
differs from the one that was pinned. That is happening now, reproducibly, on the reference
machine.

Two observations, offered as observations and not as conclusions:

- 87 C is a plausible maximum operating temperature for this part, and 75 C is low for it.
- The conditions under which 75/77/80 was captured were not recorded, so which of the two
  reflects the machine's normal state is not established. Current state is AC power, Balanced
  Windows power plan, Balanced Razer performance mode, GPU idle at P8.

### What was deliberately not done

The constant was **not** changed. Not to 87/89/92, not to accept both, not to widen the match.
Two readings and a recollection of a published specification are not grounds for editing a
safety threshold, and the standing constraints are explicit that unknown thermal state fails
closed and that observed margin semantics must not be generalised. The gate refusing to act on
limits it cannot vouch for is the system working, not the system broken - it sent no SET, and
firmware protection was never disabled.

What this does change is the standing of the allowlist as a design. It was adopted because no
independent corroborator was available, and it was reasonable on the evidence then. This finding
says the quantity it pins is not the kind of quantity an exact-match allowlist can pin. That is
a design decision for the copyright holder, recorded in the release notes rather than resolved
here.

## The anchor is the Razer performance mode

The previous entry left it open which of 75/77/80 and 87/89/92 reflected the machine's normal
state, and said the conditions under which the signature was captured were not recorded. They
are now, because the cause turned out to be trivially testable: switch the performance mode and
read the anchor back.

| Razer performance mode | Core | Margin | Anchor | Derived limits |
|---|---|---|---|---|
| Balanced (as found) | 46 | 41 | **87** | 87 / 89 / 92 |
| Silent | 47 | 28 | **75** | 75 / 77 / 80 |
| Silent, 8 s later | 47 | 28 | **75** | 75 / 77 / 80 |
| Custom, CPU low + GPU low | 47 | 28 | **75** | 75 / 77 / 80 |
| Balanced (returned) | 48 | 39 | **87** | 87 / 89 / 92 |

Same machine, same GPU UUID, same driver 610.88, same specifications (0 / -2 / -5) throughout.
The anchor follows the mode deterministically, in both directions.

Everything else was ruled out:

- **Fan mode does not affect it.** Measured at 87 with the fan mode read back as `Manual`, not
  merely commanded — the first attempt echoed the pre-apply state, which would have made the
  claim unsupported, so it was repeated with an explicit readback.
- **Temperature does not affect it within a mode.** Ten idle samples over ninety seconds gave
  anchor 87 with no variation, margin tracking temperature 1:1. The original four data points
  gave 75 across a 22 C spread (44 to 66 C).
- **Time does not affect it.**

### What this means for the signature

The recorded 75/77/80 is real and correctly measured. It is the signature of **Silent or
Custom**. The collection session simply ran with the machine in one of those modes, and
nvidia-smi corroborated it because it reports the same driver-side target and was read in the
same mode — two views of one number, not two independent witnesses.

BladeControl performs thermal control **exclusively in Balanced + Manual**. So the signature it
pinned belongs to a mode the runtime never operates in, and the mode it does operate in gives
87/89/92. That is the entire refusal.

### The design problem is bigger than the constant

Correcting 75 to 87 would make the reference machine work and would leave the real defect in
place. Two things are wrong independently of the number:

1. **An exact-match allowlist pins a value the driver is entitled to change.** The anchor is a
   thermal *target*, not a device constant. Any policy that moves it — a mode, possibly a
   battery state, possibly a driver update — breaks qualification on hardware that is behaving
   perfectly.
2. **Qualification reads it at start-preflight, which can run before the runtime has entered the
   mode it will operate in.** A machine sitting in Silent qualifies against 75, then the runtime
   takes ownership by switching to Balanced, and now operates against an 87 target having
   qualified against 75. That direction is conservative — it would act early, not late — so it
   is not dangerous. It is still not what was qualified.

Today's failure is the safe direction of the same bug: the machine was in Balanced, read 87,
found no match, and refused. Nothing was written.

The constant is deliberately still unchanged. The fix worth making is to qualify against the
mode the runtime will actually run in, and to stop treating a driver-managed target as a fixed
identity — and that is a design decision, recorded in the release notes.

## The qualify-then-switch ordering, confirmed reachable

The previous entry said qualification "can run before the runtime has entered the mode it will
operate in". That was stated as a possibility; it is now confirmed from the start path.

`BladeRuntime.StartThermalControl` calls `QualifyThermalOwnership()` before it sets
`RuntimeState.Starting` and before any mode transition. Nothing in the preconditions requires
the machine to already be in Balanced — the checks ahead of qualification are runtime state, the
emergency latch, and a standalone Manual profile. The switch to Balanced + Manual happens
afterwards, during ownership. Nothing re-reads the GPU limits after it; the post-gate freshness
check that does exist covers performance modes, not thermal limits.

So the sequence on a machine sitting in Silent is:

1. Qualification derives 75/77/80 from the Silent anchor and **matches the stored signature**.
2. The runtime takes ownership and switches the machine to Balanced.
3. The driver's thermal target is now 87, but the GPU ladder was built from the 75-based limits
   captured in step 1, and is never rebuilt.

**The direction is conservative.** A ladder built on 75 escalates roughly 12 C earlier than one
built on 87, so it over-cools rather than under-cools. Firmware slowdown and shutdown are
untouched throughout. This is a correctness defect, not a hazard, and it is recorded as such.

It is also the mirror image of today's visible failure. In Balanced the derivation gives 87, no
signature matches, and the machine is refused — the safe direction. In Silent it matches a
signature for a mode it is about to leave — the wrong-but-conservative direction. One bug, two
faces, and the exact-match allowlist cannot distinguish them because it is matching a value the
driver is entitled to change.

Not implemented here. Re-qualifying after ownership would be strictly additive — more checking,
never less — but it is a change to the start path and the ownership gate, and the outcome it
prevents is conservative rather than dangerous. It belongs with the decision it is evidence for.

## Auditing what else the mode finding invalidated

A finding that overturns a measurement should be chased through everything that cited it. Two
comments justified `GpuEmergencyTemperatureCelsius = 80` as "the hardware shutdown temperature
on the reference part". That is a statement about Silent, not about the device.

The constant is right and unchanged — as a preflight bar, the lower of the two mode-dependent
shutdown limits is the conservative choice. The justification was the problem, and it was the
dangerous kind of wrong: it invites someone comparing the constant against a live Balanced
reading to find it 12 °C low and "align" it, loosening an entry gate to match a number the
driver is entitled to move. Both comments now say what it is — a fixed policy bar, not a
reading of the device — and a test pins it so the change has to be deliberate.

The rest of the audit found nothing, which is worth recording as much as a defect would be:

- **The bar cannot trigger a live handoff.** The running loop calls `EvaluateForControlLoop`,
  whose GPU check tests presence, plausibility and freshness with no opinion about heat. At
  95 °C it still returns healthy; the graded ladder decides. This is the earlier "heat as a
  telemetry fault" separation holding up under a case it was not written for.
- **No path starts a session with null GPU limits.** `EvaluateGpuThermalSeverity` returns no
  ladder when limits are absent and relies on the start gate refusing first, which
  `GpuLimitStartGateTests` already pins across refusal, absence of Razer writes, reason
  propagation and the positive case.
- **The CPU constants stand on their own evidence.** 100 °C is Tjunction for the reference
  i9-13950HX, and the 90/85 critical-cooling pair is a policy choice with its hysteresis
  reasoning stated. Neither derives from NVML, so neither is affected.

The GPU entry gate was the only casualty, and it was a comment rather than a behaviour.

## Manual fan control works outside Balanced

The requirement changed: BladeControl should run in whatever performance mode the user chose,
not force Balanced. That turns out to resolve the GPU anchor blocker too — if qualification
happens in the operating mode, Silent derives 75/77/80 and Balanced derives 87/89/92, each
correct for its own mode, and there is no stored triple left to mismatch.

Two things stood in the way, and neither was a firmware limit.

`BuildFanControlPlan` writes `SetBalancedManualZone1/2` whenever it takes fan ownership, so
starting a session moves the machine to Balanced regardless of what the user had selected. And
`ApplyPerformanceProfile` writes every mode change with `RazerFanMode.Auto` and refuses outright
while fans are Manual. The two subsystems were mutually exclusive by our own policy.

Underneath both sat the real gate, in the packet validator:

```
allowedCombination =
    fanMode == Auto || (performanceMode == Balanced && fanMode == Manual)
```

Manual only with Balanced, enforced at the innermost layer. It came in with the original Fan
Control V1 hardware validation as a scope decision, not as a recorded finding — the pair had
simply never been sent, so nothing was known either way. `0x0D02` has always taken both values
as parameters.

### The probe, and getting it wrong the first time

The first run wrote 3800 into every mode while the machine already happened to be reporting
3800. Every row said "3800" and proved nothing: "the target held" was indistinguishable from
"nothing happened". Redone with a distinct target per mode, each having to move the value off
the previous mode's:

| Step | Result |
|---|---|
| Balanced + Manual, target 3200 | 3200, still 3200 four seconds later |
| Silent + Manual, target 3500 | 3500, still 3500 four seconds later |
| Custom + Manual, target 4100 | 4100, still 4100 four seconds later |

All three pairs were accepted and read back correctly. Neither Silent's nor Custom's own curve
reclaimed the fans.

**What this is evidence of, precisely.** `0x0D81` reports the firmware's commanded fan target —
what the controller says it is aiming for. It is not a tachometer and this does not establish
blade speed. It covers seconds on an idle machine, not sustained behaviour under load.

### The first run also found a real gap

Restoration failed. The probe left the machine in Custom + Manual, and
`ApplyFanControlProfile(Auto)` refused: "Current combination Custom + Manual is not safe for Fan
Control V1. No SET command was sent." The path back to firmware ownership was itself gated on
being in a state V1 recognises — so the one moment it was needed was a moment it would not run.
Fans were at 3800 and firmware protection was untouched, so nothing was at risk, but the machine
had to be recovered with a primitive that does not consult V1's state model.

That is worth carrying into the feature: whatever else changes, returning the fans to firmware
must not depend on the current state being one the caller already understands.

## Qualifying the anchor instead of the derived limits

Mode preservation removed the qualify-then-switch problem by construction: if ownership never
changes the performance mode, the mode at qualification time is the operating mode. What
remained was the allowlist, which still matched a single derived triple and so accepted Silent
while refusing Balanced.

The fix is to pin what is actually invariant. The T.Limit **offsets** — 0 / -2 / -5 — are static
device properties, reported identically in every mode, and they are what the original evidence
established about the interpretation. The anchor is not a device property at all; it is the
thermal target the driver is enforcing, and it follows the performance mode.

### The bounds-only version was wrong, and the existing tests caught it

The first attempt matched the offsets and then *bounded* the anchor: below the hardware's own
shutdown temperature, above a plausibility floor. Three tests failed immediately, among them the
counterexample this whole mechanism exists for — a margin measured against the slowdown limit
instead of the maximum operating temperature yields 77/79/82, which is correctly ordered,
entirely plausible, comfortably under 100 C, and two degrees too permissive.

Bounds cannot catch it, because a legitimate mode change and a mis-anchored margin are the same
shape: both are the anchor moving. So the anchors are enumerated — 75 for Silent and Custom, 87
for Balanced, both confirmed by switching modes and reading back. 77 is in neither list.

The hardware-shutdown ceiling was kept as well. It is the one genuinely independent bound
available: whatever the anchor, BladeControl must never act on a threshold above the temperature
the device says it will not survive.

### Live, on the reference machine

| Mode | Qualified limits |
|---|---|
| Balanced | 87 / 89 / 92 |
| Silent | 75 / 77 / 80 |
| Custom | 75 / 77 / 80 |

And a full Dynamic session run in Silent: `Silent + Auto` before, **`Silent + Manual` while
running** with telemetry healthy, `Silent + Auto` after the stop. The machine was never moved to
Balanced at any point.

### A packaging trap found on the way

The first three attempts to test this live tested nothing: `thermal run` is IPC-only, so it runs
inside the installed service, and the installed service was still the previous build. Worse, the
in-place upgrade kept reporting success while replacing nothing — `msiexec /i` returned 0,
`REINSTALLMODE=vomus` and even `amus` returned 0, and the binaries on disk did not change.

My first explanation for that was wrong, and is worth recording as wrong. Seeing `/x` return
1605 — "not installed under that product code" — I concluded that each rebuild generates a fresh
ProductCode and that every `/i` had been installing a new product beside the old one. Checking
instead of concluding: there is exactly one BladeControl product installed,
`{B544406D-BFA2-4D1E-9A2F-D9283AD8CC0B}`, and one uninstall entry. No side-by-side install ever
happened.

The actual cause was my own flags. `MajorUpgrade AllowSameVersionUpgrades="yes"` is configured
against a stable UpgradeCode, so a plain `msiexec /i` performs a proper major upgrade — which is
what finally worked. Adding `REINSTALL=ALL REINSTALLMODE=...` diverts that into the *repair*
path, and repair honours file-version rules: identical assembly versions, no replacement, exit
code 0. The `amus` mode that should have forced replacement did not, because repair never got
the chance to run as an upgrade. The initial 1603 was almost certainly the running service
holding its files open; later attempts stopped it first.

The 1605 was real but harmless: `/x <path-to-msi>` resolves the ProductCode from that file, and a
rebuilt MSI does carry a new one. That says nothing about how the installed product got there.

Two lessons, both about the same reflex. A build succeeding is not a deployment, and `thermal
run` executes inside the installed service however fresh the CLI is. And an exit code of 0 from
an installer is a statement about the transaction, not about the bytes on disk — the check that
settled it was reading the deployed assembly and looking for a symbol that only exists in the
new code.

## The hole that multi-mode opened

Supporting every performance mode created a failure that did not exist while every session
forced Balanced, and it was not in the plan I wrote for the change.

A session's GPU thermal limits are derived from its mode's anchor — 87/89/92 in Balanced,
75/77/80 in Silent and Custom. The mode can change from outside the session: a keyboard
shortcut, vendor software, anything. That leaves the fan mode untouched, so `IsOwnedManual`
still returns true, ownership still looks intact, and the ladder carries on with limits that no
longer describe the machine.

One direction of it is dangerous rather than merely wrong:

| Change | Real target | Ladder holds | Effect |
|---|---|---|---|
| Silent → Balanced | 87 | 75-based | fires ~12 °C early — conservative |
| **Balanced → Silent** | **75** | **87-based** | **fires ~12 °C late** |

Firmware slowdown and shutdown still stand underneath, but acting before them is the entire
point of the ladder.

The watchdog now records the mode the session qualified in and hands back to firmware when it
changes. Re-qualifying mid-session would mean a fresh NVML discovery inside the control loop,
which is the wrong place to take that on; handing back is bounded, correct, and consistent with
how every other loss of the qualified state is already treated.

### The fake hid it twice

Two properties of `FakeRuntimeHardware` had been written when Balanced was the only mode, and
both quietly asserted nothing once it was not. `ReturnToFirmwareAuto` forced Balanced, so every
recovery test passed whether or not the code preserved anything. `EnterManualBaseline` did the
same, and once the watchdog started checking the mode, that one turned into five failing tests —
the fake was entering Manual in a mode the session had not qualified in, which is exactly the
condition the new check exists to catch. The fake was wrong; the check was right.

That is the second time in this change that a test double forcing Balanced was the thing
standing between a real defect and a green suite.

## Compact window rework

The compact window is the surface most people will actually use, and it had three problems that
were all the same problem: it showed things it could not vouch for.

### The fan tile

The header was CPU and GPU. It is now CPU, FAN, GPU, and the fan figure is the one that needed
care. No physical tachometer has been found on this machine — 0x0D81 echoes the commanded
target, LibreHardwareMonitor reports no fan sensors, NVML reports fan speed unavailable — so
there is no measured RPM to show and presenting a commanded value as one would be a claim the
evidence does not support.

What it shows instead is what BladeControl actually knows, labelled as what it is:

| State | Heading | Value | Caption |
|---|---|---|---|
| Firmware Auto | FIRMWARE AUTO | — | firmware owns cooling |
| Fixed | FAN | the commanded target | target |
| Dynamic running | FAN | the effective target | dynamic target |
| Emergency handoff | FIRMWARE AUTO | — | firmware owns cooling |

Under Auto there is no BladeControl target at all, so the tile shows nothing rather than the
last number from a previous mode dressed as the current state.

### Two fan sliders became one

The window exposed Fan 1 and Fan 2 as independent sliders. The machine has two fans and the
protocol addresses them separately — which the runtime still does, and still verifies per zone —
but asking the user to set them independently offered a decision they have no basis for making
and an easy way to desynchronise the zones for no benefit. One control now drives both.

### Levels that will not be sent are shown, disabled

Custom performance offered CPU Low and Medium as hardcoded buttons and GPU as the static text
"GPU Low". The protocol models five CPU levels and three GPU levels; the rest were simply
absent, which reads as hardware that does not have them.

Both rows now list every modelled level, with the unvalidated ones visible and greyed and their
reason on the tooltip. The full Performance page already had exactly this — `PolicyOptionViewModel`
with availability and a reason — so the compact window binds the same lists through a compact
variant of the same list style, rather than growing a second visual language for the same idea.

### Emergency handoff

A dedicated panel, above the controls it invalidates, saying that firmware owns cooling, that
the service is still running, and that Dynamic will not resume by itself. It is a latched
terminal state, and describing it as in progress reads as "wait and it will resolve" when
nothing will — resuming automatically after a thermal emergency is how a loop starts.

### Not visually confirmed

The window builds, loads its resources and constructs in the WPF smoke test, and five new tests
pin the fan tile's honesty, the single control, the disabled levels and the handoff panel. Screen
access was declined, so the rendered layout has not been looked at. Recorded rather than assumed.

## RC provenance

Packaged from a known clean commit, which is the point of recording this at all.

| | |
|---|---|
| Commit | `2d18337e91a5ca6cde025fa877776e65f0a3778d` |
| Branch | `release/v0.1.0` |
| Working tree | clean |
| Tests | 781 passed, 0 failed |
| Build | Release, 0 warnings, 0 errors |
| `dotnet format` | clean |

| Artifact | SHA-256 |
|---|---|
| `BladeControl-0.1.0-win-x64.msi` | `35b3e431663360d868fc0bf72bc4ed520877a3323c68881e74af59d4b9bbf1a2` |
| `BladeControl-0.1.0-win-x64-portable.zip` | `29a9bf312bf5d3c35dff152e1b0a83d9b2e8d48cf9044884e1aeaec958c09e6a` |
| `BladeControl-0.1.0-win-x64-symbols.zip` | `c4513352e87087d167946f30db4edb250f958aeb1e92bdc51bc55cbd06f30c99` |

**This RC is deployed.** The runtime reports `Build 0.1.0+2d18337e91a5`, which is this commit.

I previously wrote here that it had not been deployed and that elevation was the blocker. Both
were wrong, and the way they were wrong is worth keeping.

**Elevation was never the problem.** `Stop-Service` and `Start-Service` both work from an
unelevated shell and were used successfully throughout this session. The one "Access is denied"
came immediately after a failed MSI transaction, while the SCM was still unwinding it, and it
cleared on its own. I read a transient condition as a permanent capability loss and wrote a
report around it, when one retry would have settled it.

**The deployment verification was a false negative.** I checked for the new code by decoding the
whole assembly with `Encoding.Unicode.GetString` and running `-match` over the result. That does
not reliably find literals in the metadata `#US` heap, so it answered "not deployed" for a file
that contained the string at byte offset 168115. A direct byte-pattern search found it in the
local build and the installed copy at the identical offset.

The distinction that matters for anyone repeating this: type and member names live in the UTF-8
`#Strings` heap and a UTF-8 scan finds them, which is why the earlier `IsOwnedManual` check was
sound. String literals live in the UTF-16 `#US` heap and need a byte-level search. The
authoritative check needs neither — the runtime reports its own build identifier over IPC, and
comparing that to the commit is one command.

That also weakens what I claimed about `REINSTALL=`. That finding used the sound UTF-8 method and
did show a real before-and-after difference, so it stands as an observation. But today a plain
`msiexec /i` was followed by a false-negative reading, so I cannot say from today's evidence
whether the plain form alone would have sufficed. Uninstall-by-ProductCode followed by install is
what was actually observed to work end to end.

## Steady-state footprint

Measured over a twenty-second window on the reference machine, 32 logical processors, with the
runtime in `Stopped` — that is, the service holding hardware ownership and serving IPC but with
no thermal session running, which is how it will sit most of the time.

| Process | Idle CPU | Working set | Private | Threads |
|---|---|---|---|---|
| `BladeControl.Service` | **0%** | 75.8 MB | 53.2 MB | 12 |
| `BladeControl.UI` | **0.081%** | 160.9 MB | 89.9 MB | 26 |

Nothing here is alarming for a Synapse replacement. The service does not poll while stopped,
which is the property that matters: an idle machine pays nothing for having BladeControl
installed. The UI's 0.081% is a WPF window with a live chart and a poll loop, and it is optional
— closing it costs the machine nothing, because the service owns the hardware independently.

The service's 76 MB working set is a self-contained .NET publish and is mostly the runtime
itself. It is worth noting rather than chasing: trimming would trade a real correctness surface
for a number nobody is complaining about.

Measured with the service running build `6e0efe5`, not the current RC. Nothing in the RC changes
polling or allocation behaviour.

## Live validation of RC 2d18337

Deployed and confirmed by the runtime's own report: `Build 0.1.0+2d18337e91a5`, which is the RC
commit. Uninstall by ProductCode followed by install is what was observed to work end to end.

### Scheduler timing, 248-cycle session in Balanced

| Measure | latest | p95 | p99 | max |
|---|---|---|---|---|
| Telemetry acquisition | 378.4 ms | 391.6 ms | 408.0 ms | 464.4 ms |
| Actuator duration | 0.0 ms | 6.4 ms | 248.5 ms | 249.8 ms |
| Cycle execution | 378.5 ms | 464.5 ms | 627.5 ms | 628.0 ms |

| Counter | Value |
|---|---|
| Completed cycles | 248 |
| Latest start-to-start | 498.3 ms |
| Slow cycles | 12 |
| Catch-up cycles | 18 |
| **Missed deadline periods** | **0** |
| Maximum lateness | 285.8 ms |
| Skipped deadlines | 0 |
| Watchdog coalesced | **0** |

Telemetry acquisition dominates and is stable rather than spiky here — p95 391.6 ms against a
464.4 ms maximum. The actuator is zero on most cycles because most cycles command nothing, and
about 250 ms when they do, which is the eight-exchange write at roughly 31 ms per exchange.

The arithmetic of a slow cycle is visible in the numbers: 378 ms of acquisition plus 250 ms of
actuation is 628 ms, and 628.0 ms is exactly the observed maximum cycle. A cycle that writes
overruns a 500 ms period; a cycle that does not, does not. Twelve of 248 cycles wrote at a moment
that pushed them over.

### Architecture decision gate: case B

Occasional bounded overrun, fully recovered, with critical response still acceptable. **Retain
the simple architecture.**

The evidence for that rather than case C is `Missed deadline periods = 0` across 248 cycles. The
schedule uses absolute deadlines, so a late cycle is followed by catch-up cycles that close the
gap rather than by accumulating drift — 18 catch-up cycles for 12 slow ones, and start-to-start
back at 498.3 ms. Maximum lateness 285.8 ms.

Worst case for noticing a newly critical temperature: a sample can go critical just after an
acquisition begins, so the wait is that cycle (up to 628 ms) plus the next acquisition (about
378 ms) before a decision, plus about 250 ms to command it — roughly 1.25 s. Firmware slowdown
and shutdown are unaffected throughout and are not part of this budget.

Choosing case C on these numbers would mean introducing threading into the hardware path — sole
HID ownership, sample ordering, actuator serialisation, emergency preemption — to remove an
overrun that never misses a deadline. That trade is not justified by this data.

### Watchdog coalescing does nothing on this workload

`Watchdog coalesced = 0` across 248 cycles. The optimisation is correct and its tests pass; it
simply never fires here. It requires a fan write's post-write `0x0D82` pair to land within one
control period of a due watchdog, and with the watchdog interval at five seconds and writes
occurring on a small minority of cycles, that coincidence did not occur once.

Recorded rather than removed. It costs nothing when it does not fire, and a busier curve with
frequent target changes is exactly where it would. But it should not be described as a
contributor to the timing above, because on this evidence it contributed nothing.

### Stop and restoration

| Check | Result |
|---|---|
| Stop reported | "firmware Auto handoff performed" |
| Firmware fan mode, read directly | **Auto** |
| Performance state | Balanced, CPU Medium, GPU Low — as found |
| Runtime state | Stopped |
| Post-stop watchdog | labelled "historical; not current firmware state" |
| Post-stop watchdog value | **Balanced + Auto**, not a stale Manual |

The last row is the one that used to be wrong. The observation reflects the state after the
handoff because the recovery's own readback is adopted, so a stopped session no longer reports
the Manual mode it was holding a moment earlier.

### Service lifecycle

`Stop-Service`, `Start-Service` and `Restart-Service` all succeed from an unelevated shell. The
System log carries only Information-level SCM entries. No `.NET Runtime`, `Application Error` or
Windows Error Reporting events referencing BladeControl in the window covering the whole session,
the stop, the restarts and the reinstall.

## Installer lifecycle, and where PawnIO actually lives

Uninstall by ProductCode then install, both exit 0, verified after:

| Check | Result |
|---|---|
| Install directory after uninstall | removed |
| Service after install | `AUTO_START`, LocalSystem, correct binary path |
| Failure actions | RESTART ×2, 20 s delay, 86400 s reset |
| `LICENSE.txt` in payload | present, 35,149 bytes |
| `THIRD-PARTY-NOTICES.md` | present |
| PawnIO files bundled | **zero** |
| User settings | preserved — `ui-settings.json` still carries its pre-uninstall timestamp |
| Orphan IPC hosts | none; one process, the running service |

Settings surviving is visible in the modification time rather than merely in the file existing:
it predates the uninstall, so the file was left alone rather than recreated.

### A check of mine that was looking in the wrong place

I tested for PawnIO at `C:\Windows\System32\drivers\PawnIO.sys` and found nothing, while
telemetry was reporting healthy and CPU package temperature was being read — which should have
been the tell, and was.

PawnIO is INF-installed, so it lives in the driver store:
`C:\WINDOWS\System32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys`,
with its kernel service RUNNING. The doctor already reports this path under `PawnIoProvenance`
and had done all along. Present, external, not bundled, exactly as intended.

That is the third check in this session where my own verification method was the thing that was
wrong. The pattern is consistent enough to name: when a probe contradicts a system that is
visibly working, suspect the probe first.

## Reboot acceptance — not performed

Deliberately not done. Rebooting the reference machine is disruptive to whoever is using it and
ends the session driving the validation, so it is recorded as a human-required acceptance item
rather than claimed.

What is already known without a reboot: the service is `AUTO_START` under LocalSystem with
restart-on-failure configured and validated, a hard-killed process is recovered by the SCM in
about 25 s and returns the firmware to Auto, and the runtime refuses a second host through the
ownership gate. What a reboot would add is confirmation that these hold from a cold start, that
the UI reconnects, and that Dynamic does not resume on its own.

## Cold-boot acceptance — performed

A real reboot was performed on the reference machine. Boot at 2026-08-23 14:53:13; everything
below happened without me touching the service.

| Check | Result |
|---|---|
| Service start | **automatic, unaided** — Running by 166 s |
| Start type | `AUTO_START (DELAYED)`, `DelayedAutostart=1` |
| Build | `0.1.0+2d18337e91a5` — the RC |
| Duplicate hosts | none; one process |
| Firmware state at boot | **Custom + Auto** — firmware owns cooling |
| Dynamic self-resume | **did not** — runtime `Stopped` |
| PawnIO | service RUNNING, external, unbundled |
| Boot-time crash events | none (`.NET Runtime`, `Application Error`, WER all clear) |
| UI reconnect | connects to the cold-booted service |

### The reboot exercised multi-mode without being asked to

The machine came back in **Custom**, not the Balanced I left it in. That turned the cold boot
into an unplanned test of this session's main change, and it passed:

- Qualification derived **75/77/80** — Custom's own anchor — and reported
  `ThermalOwnershipReady: true`.
- A session started and ran as **Custom + Manual**, healthy, with the mode preserved.
- Stop performed the firmware Auto handoff, and a direct read afterwards showed fan mode
  **Auto**, performance **Custom**, CPU Medium, GPU Low — as the machine booted.
- The post-stop watchdog reads `Custom + Auto`, labelled "historical; not current firmware
  state" with its age.

Before this session that same boot would have gone one of two ways: the old code would have
forced the machine to Balanced to take the fans, or — with the anchor pinned to a single derived
triple — would have refused to start at all once the mode and the pinned signature disagreed.

### I called it a failure before it had a chance

Two minutes after boot I read `Stopped` and wrote it up as a genuine cold-boot failure. It is a
*delayed* auto-start service; it starts around two minutes in by design, and it was Running at
166 s. There were no error events precisely because nothing had failed — an absence of events
should have made me check the clock rather than reach for a diagnosis.

Two other probes of mine misfired in the same minutes: `Split(' ')[0]` on an `ImagePath`
containing "Program Files" reported the service binary missing, and the earlier PawnIO check
looked in `System32\drivers` for an INF-installed driver. Neither was a real defect. Every
"failure" found in this cold-boot pass was mine, and the system under test was correct each time.

## The full app's emergency banner said one thing and looked like another

The compact window got a dedicated emergency panel; the full app was left with the generic
runtime-alert banner, and auditing it against what was actually asked for turned up two
problems.

**The colour contradicted the sentence.** The banner was hardcoded to the danger palette, so an
emergency handoff rendered in red while its own text read "The machine is safe." Protection
having worked is not protection having failed, and that distinction was already drawn in
`Display.EmergencyHandoff`, which had been moved to Warning earlier for exactly this reason. The
banner had not followed. It now takes its tone from the state: Warning for a handoff, Danger for
a fault, because a fault is a fault.

**It left out the two things a person actually needs.** The wording said what happened and to
restart, but not that the service is still running, nor that Dynamic will not resume on its own.
Both matter: without the first, "handed back to firmware" reads as though BladeControl has died;
without the second, waiting looks like a reasonable option when nothing is going to happen.

Both are covered by a test that also pins a genuine fault staying red, so the two cannot drift
back together.

### Compact window sizing

Not visually confirmed — screen access was declined. The risk is bounded by construction: the
window is `SizeToContent="Height"` with `MaxHeight="596"` and its content sits in a
`ScrollViewer` capped at 490, so added content scrolls rather than clips. That is a reason not to
worry about truncation, not a substitute for looking at it.

## 0x0D81 is an echo, proved by a step response

The request was to show current fan speed alongside the target rather than only the target. That
required settling, rather than assuming, whether any current speed is actually available. One
loose thread justified re-opening it: a single earlier reading showed fan 1 at 0 while fan 2 read
3800, when both had been commanded identically, and an echo should not be able to do that.

Sampling `0x0D81` at 250 ms through a step in both directions, and through the handover to
firmware Auto:

| Phase | Command | Reading |
|---|---|---|
| A, as found (Auto) | none | 4500 for 8 samples |
| B | 2000 | 2000 on the first sample, flat for 12 |
| C | 4500 | **4500 on the first sample**, flat for 24 samples over 6 s |
| D | 2000 | 2000 on the first sample, flat for 20 |
| E, firmware Auto | none | **2000, frozen for 16 samples over 7 s** |

A 2500 RPM step is crossed with no intermediate value, in either direction, at 250 ms resolution.
Fans have inertia; a tachometer cannot do that.

Phase E settles it beyond argument. Under firmware Auto BladeControl commands nothing, and the
controller is running its own curve — yet the reading sits frozen at 2000, the last value *we*
wrote. Phase A had shown 4500 under Auto for the same reason: it was the last value written
before that. The register holds the most recent commanded target and nothing else.

The 0/3800 reading that reopened this was not evidence of measurement. It was a single transient
at the very start of a probe, and it did not reproduce.

### Every candidate source is now exhausted

| Source | Result |
|---|---|
| Razer `0x0D81` | **echo of the last command** — proved above |
| LibreHardwareMonitor | zero Fan, Control and Flow sensors, elevated and not |
| NVML `fan.speed` | `[N/A]` |
| WMI `Win32_Fan` | two cooling devices, speed fields empty |

There is no measured fan speed on this machine. The compact window will keep showing the target,
labelled as a target, and nothing under firmware Auto — which is not a limitation of the
interface but the complete truth of what the hardware reports.

Probing undocumented opcodes for a hidden tachometer register was not attempted and is out of
scope under the standing safety boundary.

## UI batch: levels, cooling profiles, slider, charts

### Performance levels

CPU High and Boost and GPU Medium and High are sendable. They were blocked because they had not
been exercised here, which is a reason to be careful, not a reason to withhold stock controls
Synapse exposes to anyone. The packet shape, echo verification, thermal ladders and firmware
protection are all unchanged; the EC refuses anything it does not accept and the echo check
catches it.

**Overclock is removed**, at the owner's request, so BladeControl cannot interfere with tuning
done in XTU. It stays a *readable* value — a machine already sitting in Overclock is reported
accurately rather than as unknown — and is refused at the policy layer and absent from the UI.
That distinction is the whole point: reading a state is not offering it.

### Cooling: named firmware profiles instead of "Auto"

"Auto" was one button meaning "give the fans back", which said nothing about what firmware would
then do. Firmware's fan curve *is* the performance mode's curve, so the cooling row now offers
**Silent** and **Balanced** alongside Fixed and Dynamic. Picking one hands the fans back and says
which curve takes over.

It costs no extra write: applying a performance mode already writes fan mode Auto, so this is one
operation. The fan tile follows, reading "silent firmware curve" or "balanced firmware curve"
rather than a generic owner.

### The slider was wearing the stock template

Only `Foreground` and `Margin` were set, and neither touches what looked wrong: the default WPF
template is drawn for a light theme — pale sunken groove, hairline ticks, small grey thumb — so
on this background it read as a disabled control rather than the main way to set a fan target.

It now has a real template: rounded track, travelled portion filled with the accent so the value
reads at a glance, and a thumb big enough to grab with a hover halo. One style, used by both
windows, because it is the same control doing the same job in each.

### Charts on three more pages, from one history

Dashboard gets temperatures, Fans & Thermal gets the fan target beside the fan controls, and
Performance gets package power — which is where a mode or level change actually shows up, rather
than as a number that merely moved.

All three draw from the single `TelemetryHistory` that Monitoring already collects. A second
buffer would drift from the first the moment either missed a sample, and then two pages would
disagree about what happened.

That did require widening one thing. Monitoring's presentation flag doubled as "is Monitoring
selected", because it used to be the only page with charts; gating on that alone would have left
the new charts collecting samples and never repainting. One `ChartsAreOnScreen` predicate now
decides it for every chart-bearing page.

## Performance and cooling are independent, and now behave that way

Applying a performance mode wrote fan mode Auto. So changing a power setting silently took the
fans back from whoever had them: a fixed target was discarded, and a running Dynamic session
would have had its ownership pulled out from under it.

This is the exact mirror of the bug fan control had in the other direction, where taking the
fans forced Balanced. That one was fixed; this one was not, and the asymmetry is what made it
easy to miss — one half of the pair had been corrected and the other still looked like it
worked.

`0x0D02` carries performance mode and fan mode as a pair, so changing either means restating the
other. The question is only what to restate it as, and the answer is "whatever it already was".
Both directions now preserve.

Seven places assumed Auto:

| Site | Was | Now |
|---|---|---|
| `WritePerformanceMode` | forced Auto | removed; callers write the pair explicitly |
| plan builder | "already correct" required Auto | compares performance mode only |
| `ValidatePerformanceState` | **refused outright when Manual** | fan mode not consulted |
| verification | expected Auto | expects the mode that was preserved |
| `ValidateRestorationState` | required Auto | fan mode not consulted |
| `TryCreateRestorationProfile` | **returned false when Manual** | fan mode not consulted |
| self-test precondition | requires Custom + Auto | unchanged; it is a self-test |

The two marked in bold were the worst. Refusing to act while Manual meant the only way to change
power mode during a Dynamic session was to stop cooling first. And returning no restoration
profile when Manual meant a performance apply that failed part-way during a session attempted no
recovery at all — the one situation where recovery matters most.

Nothing failed when this was changed, which is the other finding: no test pinned the coupling in
either direction. Two now do.

### The UI was wrong the same way

Yesterday's cooling row offered Silent and Balanced as firmware fan profiles. That only worked
*because* of the bug — applying a mode happened to hand the fans back, so the buttons appeared to
do a cooling thing. With the axes separated they would set a power ceiling and leave fan
ownership untouched, which is not what a cooling control should do.

Cooling is fan ownership: **Firmware · Fixed · Dynamic**. Which firmware curve is running is
named beside it — "following the Silent curve" — as context rather than as a second copy of the
performance selector.

### The level restriction in restoration was stale too

`TryCreateRestorationProfile` only restored CPU Low or Medium and GPU Low, which was the old
policy set. It now refuses only Overclock, which is the one level this build cannot write and
therefore cannot restore to.

## Visual identity: verified against real pixels, not filenames

The app shipped with no icon at all — `ApplicationIcon` was declared empty, and the tray icon
was hard-coded to `SystemIcons.Application`. A delivered asset set (app icon, three tray-state
icons, a social preview card, a README banner) was checked before anything was wired in, using
Pillow rather than trusting the accompanying notes.

Two real defects, neither visible from the filenames:

- **`app.ico` had one embedded frame (256×256), not the claimed 16/32/48/256 set.** Rebuilt from
  the 1024px master with real 16/24/32/48/256 frames. The three tray-state ICOs were rebuilt the
  same way, and the small frames were verified pixel-identical (0.00% difference) to the
  hand-checked 16/24/32 exports rather than silently re-derived from a larger source.
- **My own first verification script reported the rebuild as broken.** It used `.seek(i)` to
  enumerate ICO frames, which is the animation-frame API (GIF/TIFF) and doesn't apply to ICO —
  Pillow addresses ICO frames by size via `.ico.getimage(size)`, not by sequence. Caught by
  testing the claim in isolation before reporting it, rather than passing the false negative
  along.

Wiring in the tray icon surfaced an unrelated real bug: a relative pack URI
(`new Uri("Assets/tray-idle.ico", UriKind.Relative)`) resolves against whatever WPF considers
the current entry assembly. That is `BladeControl.UI.exe` when the app runs normally, but not
under the WPF smoke test, which hosts the same assembly from a test-runner exe — so the resource
genuinely could not be found there, and the smoke test caught it immediately. Fixed with the
fully-qualified `pack://application:,,,/BladeControl.UI;component/...` form, which does not
depend on which assembly is currently "the application."

Icon selection reuses `Display.RuntimeStateTone` rather than a new mapping: `Faulted` is the
emergency-red icon, `EmergencyHandoff`/`Starting`/`Stopping` are the amber one — matching this
project's standing distinction that a completed, verified handoff is a safe state, not an
alarm — and everything else, including offline, is the idle green.

The social preview card needed two rounds after the prompt-brief stage: the first render put the
mark on an opaque white plate, contradicting the dark-background spec and the transparent
treatment every other asset uses; the second fixed that but exported at 1774×887 instead of
1280×640. Same 2:1 aspect ratio in both, so the second issue was a lossless resize, not another
generation — Lanczos-downsampled to spec and placed at `.github/social-preview.png`.

Confirmed end-to-end rather than assumed: `Icon.ExtractAssociatedIcon` against the built `.exe`
to prove the Win32 icon resource is genuinely embedded, not just accepted by MSBuild; a direct
launch of the built UI to prove the tray icons load at runtime; the full test suite; and
`dotnet format --verify-no-changes`.

---

## v0.1.3: three defects the screenshots found, and one the screenshots explained

Three reports, one screenshot each, and each turned out to be a different kind of bug.

**"Live — 1 s old" beside a stopped runtime.** The temptation is to call this a labelling
nit. It is not: the sample really was one second old, because the interface keeps polling the
provider while idle, so the *freshness* claim was true. What was false was the implied claim
underneath it — that BladeControl was driving the fans. The dashboard reached its live branch
whenever the sample was fresh and the state was not Stopped, while the tone property directly
below it already tested `IsStopped` on its own. The two halves of the same badge disagreed, and
the colour was right while the word was wrong.

Two things worth recording. First, the compact panel had already solved this correctly and
called the state "Monitoring snapshot" — the fix was to adopt the wording that already existed,
not to invent one. Second, generalising the rule from "not Stopped" to "only Running" closed a
latent case nobody had reported: Faulted and EmergencyHandoff also claimed live telemetry, in
exactly the situations where cooling had just gone back to firmware. `Display.IsLiveSession`
now holds that rule once, for both surfaces.

**"Sliders are white."** The fan sliders were not white. Rendering the theme in isolation
showed them correctly green, enabled and disabled, which meant the report and the code
disagreed — so the next step was to render the real shell rather than argue with the user.
`RenderTargetBitmap` over `MainWindow`, page by page, at the same size the window actually
runs, produced the answer immediately: a near-white system **scrollbar** down the right edge of
every scrolling page, and white system **checkbox** glyphs. Neither control had ever been
templated. What the user called a slider was the scrollbar, and they were right that it was
white.

The instructive part is that this is the third instance of one defect class. `ComboBoxItem`
was the first, and it already had a test — the popup rendered near-black on near-black. A
control left to system defaults in a dark application is not merely unstyled, it is
*inverted*. `SubtleCheckBoxStyle` is the sharpest illustration: it set `Foreground` and
`FontSize` and looked like styling, but the box glyph a user actually sees is drawn by the
template, so the white square survived untouched. The new test therefore asserts a
`Template` setter, not the presence of a style, and asserts that the keyed variant is
`BasedOn` the themed one rather than standing alone.

**"GPU details are missing."** This one is not fixed, and the first answer I wrote about it
was wrong in a way worth recording. The service gets temperature and clocks from NVML and gets
`NVML_ERROR_UNKNOWN` (999) for power and utilization, while the CLI — same provider code, same
P/Invoke signatures, same machine, minutes apart — read 26.6 W. Two data points, one clean
story: LocalSystem cannot read what an interactive process can. I wrote that into
known-limitations.md.

Then I deployed the build and probed the installed service, and utilization came back valid.
That single sample falsified the story I had just documented, so I went looking properly
instead of explaining it away. Sampling the service repeatedly, and again while holding the
dGPU awake with `nvidia-smi -l 1`, separated what had looked like one defect into two:
utilization is intermittent and tracks the GPU's power state, answering while the part is
active and refusing while it idles; power never answers the service at all.

The finding that actually matters came from checking the value rather than the return code.
`nvidia-smi` itself reports **593.51 W** for this GPU, and three consecutive CLI reads gave
593.5 W, 30.1 W, 30.1 W. The part is rated near 150 W. So GPU power on this machine is not a
metric the service is being denied — it is a metric the driver reports incorrectly, to
NVIDIA's own tool as readily as to us, with absurd values scattered among plausible ones. That
is the shape this project has already refused once, in exactly these words, for fan RPM: a
number that looks like a measurement and is not one is worse than an honest dash.

Two lessons, and the second is the one I nearly missed. A clean story from two data points is a
hypothesis, not a finding, and deploying gave me the third sample that broke it. And when a
provider refuses a value, check what it returns when it does *not* refuse before deciding the
refusal is the bug.

What *was* a real defect here, and got fixed, is that Diagnostics reported "Power: Yes"
throughout. `GpuPowerSupported` tested `IsSupported`, which only asks whether the driver
declined with `NotSupported`; a generic failure leaves it true. The CPU counterpart three lines
below tested `IsValid && HasValue`. So the capability flag said a value was available while the
card beside it showed a dash — the panel was contradicting itself again, exactly as the "Live"
badge had. It now means what the CPU flag means.

`GpuTemperatureSupported` was deliberately left alone despite looking identical. It feeds
qualification, and a transient failed read must not de-qualify the GPU mid-session; the
qualifier checks the actual sample separately. Two flags that look the same are not the same
when one is display and the other is a safety gate.

**Method note.** Rendering the real shell to PNG and reading the pixels, rather than reasoning
about XAML, found the scrollbar in one step after two wrong hypotheses (disabled-state knob
colour, then `MutedBrush`). The probe was a throwaway test class, deleted once it had done its
job — but it is worth remembering that the visual tree can be rendered headlessly and looked
at, which for a theming defect beats reading the stylesheet.

Two stale README claims surfaced while sweeping versions: that CPU High/Boost and GPU
Medium/High appear greyed out, which stopped being true when the level policy opened up, and
that live telemetry includes GPU power and utilisation, which the service does not receive.
Both corrected.

---

## Correction: the GPU telemetry entry above was wrong twice before it was right

Appended rather than folded into the entry above, because that entry is now part of the record
of how this went, and the going is the point.

**First version.** The service gets `NVML_ERROR_UNKNOWN` for GPU power and utilization; the CLI
reads them. Two callers, two results, one obvious difference — LocalSystem in session 0 versus
an interactive process. Written into known-limitations.md as a finding, with a note that the
confirming experiment had not been run.

**Second version.** Deploying the build and probing the installed service returned a valid
utilization on the first sample, which the first version said was impossible. Sampling
repeatedly, and again while holding the dGPU awake with `nvidia-smi -l 1`, split it into two
problems: utilization intermittent and tied to GPU power state, power never answering the
service at all. That version also caught the genuinely important thing — `nvidia-smi` itself
reporting 593.51 W for a part rated near 150 W, so the value is untrustworthy independent of
who asks.

**Third version, and the measurements this one actually rests on.** Redeploying and probing
again returned valid *power* from the service — 33.9 W — which the second version said never
happened. So this time, instead of writing up two more data points, I sampled properly:
`nvidia-smi` and the service read back to back, 38 consecutive samples at 1.5–5 s intervals,
recording performance state alongside both. Zero refusals. Service power tracked `nvidia-smi`
to within rounding across P0, P5 and P8 — 20.1 against 20.08, 10.3 against 10.32, 9.9 against
9.88.

So both metrics are intermittent, neither is denied to LocalSystem, and the earlier failures
correlate with an idle machine that nothing was polling. The power-state mechanism explains
every observation and remains a hypothesis; the intermittency and the accuracy-when-present are
measured.

**What went wrong twice, and it was the same thing both times.** Two data points with an
obvious difference between them is a hypothesis wearing a finding's clothes. Both wrong versions
had a mechanism that sounded right — privilege boundary, then power state — and both were
written up before anything had been sampled enough to have a distribution. The third version is
different not because the story is better but because there are 38 paired samples behind it
instead of two.

The tell was available each time and I did not act on it until the third: the claim was always
of the form "X never happens", and "never" from a handful of observations is the cheapest
possible statement to falsify. When the next sample falsified it, that was not bad luck.

Worth keeping from this: the 593.5 W outlier is still real and there is still no plausibility
gate on GPU power, so an absurd value can reach the display. known-limitations.md now says that
in those words — a known gap, not a decision — rather than implying the refusal protects us
from it.
