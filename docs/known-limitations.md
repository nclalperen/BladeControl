# Known limitations

Stated plainly, because a limitation you find in the code is worse than one you were told.

---

## Hardware scope

Everything in this project is validated on exactly **one** machine:

| | |
|---|---|
| Model | Razer Blade 16, RZ09-0483 |
| CPU | Intel Core i9-13950HX |
| GPU | NVIDIA GeForce RTX 4090 Laptop GPU |
| GPU driver | 610.88 |
| Motherboard ID | SO690 |

**No claim is made about any other Razer model, any other GPU, or any other firmware revision.**

The GPU thermal thresholds are gated on a validated signature — an exact GPU identity paired
with the exact limits it was observed to produce. A GPU that is not on that list gets no
thresholds, and Dynamic Cooling refuses to start rather than guessing. That refusal is the
designed behaviour on unvalidated hardware, not a defect.

The Razer write families used are the ones already validated on this machine. Nothing probes or
fuzzes undocumented commands.

---

## Physical fan RPM is not available

`0x0D81` returns the **commanded target echoed back**. It is not a tachometer, and the product
does not present it as one — it is labelled *firmware-reported fan state* everywhere it appears.

Four read-only sources were examined on the validated hardware:

| Source | Result |
|---|---|
| LibreHardwareMonitor Fan / Control / Flow sensors | zero sensors, elevated and non-elevated |
| NVML / `nvidia-smi` `fan.speed` | `[N/A]` — the GPU fan is EC-controlled, not driver-controlled |
| WMI `Win32_Fan` | two cooling devices present, speed fields empty |
| Razer HID `0x0D81` | the commanded target |

Actual RPM is therefore reported as **unavailable**. Presenting `0x0D81` as RPM was specifically
rejected: it equals the commanded target by construction, so it would agree with itself whatever
the fans were physically doing, including not spinning at all.

---

## Scheduler: write cycles exceed the control period

Measured on the reference machine over a live session, 500 ms requested period:

| | latest | p95 | p99 | max |
|---|---|---|---|---|
| Telemetry acquisition | 374.4 | 389.8 | 398.9 | 400.5 ms |
| Actuator (8-exchange write) | 0.0 | 243.5 | 244.5 | 244.9 ms |
| Whole cycle | 374.5 | 621.4 | 624.2 | 625.5 ms |

Maximum lateness 136.4 ms, **zero** whole periods lost, 10 slow cycles in 128.

The arithmetic is the limitation: acquisition alone consumes roughly 390 ms of the 500 ms
period, so **any cycle that also writes a fan target overruns**, by roughly 125 ms. Reducing the
write from sixteen HID exchanges to eight halved that cost — it did not eliminate it, because
about 30 ms per HID exchange times eight is still ~244 ms and eight is already the minimum that
can be verified.

Consequences, measured rather than assumed:

- The schedule self-corrects. Deadlines advance absolutely, a late loop runs back-to-back until
  it catches up, and no period was ever skipped.
- **Worst-case detection delay for a newly critical temperature is roughly one control period
  plus one cycle execution — about 1.1–1.25 s** depending on session. For the three-sample
  emergency ladders (1.5 s minimum by construction) this is comfortable. For the single-sample
  immediate handoffs — CPU 100 °C, and the GPU's own hardware-shutdown-minus-one-degree point,
  which is 79 °C in Silent/Custom and 91 °C in Balanced (see
  [mode-dependent GPU limits](#thermal-control-runs-in-the-mode-you-chose-and-only-that-mode)
  below) — it means up to about a second of delay against 1 °C of margin below the hardware
  shutdown point, in whichever mode the session is running.
- Firmware slowdown (77 °C) and hardware shutdown (80 °C) remain in force throughout and are not
  BladeControl's to defeat. They are the backstop; the ladder above is the first line, not the
  only one.

**This is accepted for v0.1.5 and documented rather than fixed.** Removing it means separating
acquisition from actuation across threads, which introduces sole-HID-ownership, sample-freshness
and preemption invariants that deserve their own design pass rather than being bolted onto a
latency patch. The per-component statistics exist precisely so that decision can be made on
distribution data.

Telemetry acquisition is **variable, not constant**: observed between roughly 160 ms and 400 ms
across sessions on the same machine. Any future redesign should rest on a distribution, not on a
single snapshot.

---

## GPU power and utilization are intermittent, and power is occasionally absurd

The GPU card sometimes shows a temperature with `—` for power and utilization. Both metrics
come and go; neither is permanently unavailable, and the dash means "not right now", not
"never".

**Both are intermittent, and it is not a privilege or session-0 restriction.** Two rounds of
measurement, on the same build and the same machine:

| Condition | Samples | Power | Utilization |
|---|---|---|---|
| Machine idle, no client polling continuously | ~10 across two sessions | `NVML_ERROR_UNKNOWN` (999) | mostly 999, occasionally valid |
| Under continuous polling (one read every 1.5–5 s) | **38 consecutive** | **all valid** | **all valid** |

Under the second condition the service's readings tracked `nvidia-smi` to within rounding —
20.1 vs 20.08 W, 10.3 vs 10.32 W, 9.9 vs 9.88 W — across observed performance states P0, P5
and P8. So the LocalSystem service can read both metrics perfectly well; an earlier revision of
this section claimed it never could, on the strength of two data points, and that was wrong.

The pattern is consistent with the discrete GPU's power state: the counters are unavailable
while the part is in a deep power-saving state and answer once it is active, and a client that
polls frequently keeps it active. Temperature and clocks answer in both conditions. **That
mechanism is a hypothesis** — it explains every observation to date and nothing has been done
to prove it.

**Power additionally returns a physically impossible value from time to time.** Three
consecutive reads through `BladeControl.Cli` gave **593.5 W**, 30.1 W, 30.1 W, and
`nvidia-smi --query-gpu=power.draw` independently reported **593.51 W** in the same period. The
RTX 4090 Laptop GPU here has a total graphics power near 150 W. This is the driver, not our
marshalling, and it is rare — 38 later samples were all plausible — which is precisely the
dangerous shape: a metric that is usually right and occasionally absurd.

That is the same judgement already reached about [fan RPM](#physical-fan-rpm-is-not-available):
a number that looks like a measurement and is not one is worse than an honest dash. A
plausibility gate now reads NVML's current power-management limit once when the provider opens
and marks a successful sample above that limit plus a 25% transient margin as invalid. The raw
sample is retained with a diagnostic naming both values; it is not clamped or displayed. A
refused limit query fails open and leaves power samples unchanged, while a refused power sample
continues to carry NVML's own return code.

The 25% allowance is deliberately broad, not a measured transient envelope. Instantaneous draw
can legitimately cross a sustained management boundary, while the observed 593.5 W outlier is
still separated comfortably from the roughly 150 W device limit. Tightening the margin needs
correlated peak-load samples. The policy is verified against fake NVML responses; it has not yet
been exercised against a captured legitimate transient on hardware.

**Nothing in the control path depends on either metric.** The thermal ladders run on
temperature, which is reliable in every condition observed. This costs display detail and no
capability. Diagnostics reports the metric as unavailable and carries NVML's own return code;
the dashboard's dash has that same reason as its tooltip.

---

## Diagnostics

- Only a **Running** session reports current readings. Stopped, Faulted and EmergencyHandoff all
  present the last thing observed, labelled as history.
- A metric an older runtime did not send is shown as *not reported*, never as zero.
- A runtime that has not yet run a session says so ("No session has run since the runtime
  started") instead of printing a table of zeros under a "Healthy" heading.
- Runtime diagnostics — total events, last failure, emergency status — are reported
  independently of scheduler history. They describe the process lifetime, not one session, and
  a crashed-and-restarted runtime has the former without the latter.

---

## Thermal control runs in the mode you chose, and only that mode

BladeControl takes fan ownership in whatever performance mode the machine is already in and
leaves it there. Silent stays Silent, Custom stays Custom. It no longer moves the machine to
Balanced to take control of the fans, and it no longer moves it to Balanced to give them back.

This matters beyond preference, because the GPU thermal limits are mode-dependent. The anchor
the derivation rests on is the thermal target the driver is currently enforcing, and it follows
the performance mode:

| Mode | Anchor | Qualified limits |
|---|---|---|
| Balanced | 87 | 87 / 89 / 92 |
| Silent | 75 | 75 / 77 / 80 |
| Custom (CPU low, GPU low) | 75 | 75 / 77 / 80 |

Each mode therefore qualifies against the limits that are actually correct for it. Fan mode does
not affect the anchor (verified at 87 with the fan mode read back as `Manual`), nor does
temperature within a mode, nor elapsed time.

### Changing performance mode ends a running session

The limits in force were derived for the mode the session qualified in. If the mode changes
underneath it — a keyboard shortcut, vendor software — the fan mode is untouched, so ownership
would still look intact while the ladder used limits that no longer describe the machine.
Balanced to Silent is the direction that matters: the real target drops to 75 while the ladder
still holds 87-based thresholds.

The runtime detects this and hands the fans back to firmware. That is deliberate: re-qualifying
mid-session would mean a fresh NVML discovery inside the control loop. Change modes first, then
start Dynamic.

### An unrecognised anchor fails closed

Qualification pins the T.Limit offsets, which are static device properties, and matches the
anchor against the values observed on that part. An anchor that is neither 75 nor 87 is refused
even if it looks entirely reasonable — a margin measured against the wrong reference yields
77/79/82, which is correctly ordered, plausible, under the hardware shutdown temperature, and
two degrees too permissive. A mode or driver policy producing a new anchor is refused until it
has been checked, which may mean a legitimate configuration is turned away.

---

## Crash recovery leaves a window where nothing owns the fans

A hard-killed runtime — `TerminateProcess`, a power event, a service crash — runs no managed
cleanup, so the fans stay at the last speed the session commanded, in Manual, with nothing
driving them. The Service Control Manager restarts the service, and host initialisation performs
a one-time recovery back to firmware Auto before it will serve anything.

Re-measured on v0.1.4 by killing the service process mid-session, in Custom mode:

| Step | Observed |
|---|---|
| Service process killed | fans held at last commanded speed, **Custom + Manual** |
| Machine-wide ownership gate | **released by the kernel on process death** — the restarted host reacquires it |
| SCM restart | **20 s** (configured `RESTART -- Delay = 20000 ms`) |
| Firmware state after restart | **Custom + Auto** — recovery succeeded, and the mode was preserved |

The earlier measurement of this recorded Balanced in both rows. That was correct at the time and
is not any more: taking fan ownership used to move the machine to Balanced, and since v0.1.1 it
does not. Performance mode is preserved across the crash and the recovery, so the mode you are
in is the mode you come back to.

The gate row matters because the ownership gate became machine-wide in v0.1.4, and a lock that
outlived the process holding it would turn a crash into a permanent outage — the restarted host
could never acquire it. It does not: the semaphore was observed free within four seconds of the
kill, and held again once the service was back.

A clean stop is a different path and does not have this window at all. Verified separately on
v0.1.4: with a session running and the fans in Manual, `net stop` returned the machine to
firmware Auto before the service exited.

So the exposure is roughly **20–25 seconds during which BladeControl does not own cooling**.
The direction of that failure matters: the fans are stuck at a *commanded* speed, which under
a rising load is under-cooled relative to what the curve would have asked for, and under a
falling load is over-cooled. Firmware slowdown and hardware shutdown remain in force for the
whole window and are never disabled, so the machine is protected by the same thresholds that
protect it with BladeControl uninstalled.

If the recovery itself fails, the runtime faults and refuses to serve rather than pretending it
owns hardware it does not; the fans then remain in Manual until a person intervenes. This is
reported — `Last failure` names it — and is deliberately not retried in a loop.

---

## A second runtime opens hardware before it learns it is not allowed to run

`RuntimeWindowsHost` opens the Razer HID session and the telemetry session first, and acquires
the ownership lease afterwards, inside `InitializeHost`. Starting a second host while one is
already running therefore opens a read-only HID handle and initialises LibreHardwareMonitor
before the gate refuses it and the process exits.

This is a layering flaw, not a safety hole. The refused host performs no writes, the gate
serialises all control, and the refusal message is accurate and actionable ("Stop the
'BladeControl Runtime' service before running a console host"). Observed directly: the portable
runtime, started while the installed service was live, got as far as both hardware sessions and
then exited at the gate.

**That observation predates the gate actually being machine-wide, and could not have shown
what it was read as showing.** Until v0.1.4 the semaphore was named `Local\…`, which is scoped
to one Windows session; the service runs in session 0 and a console host or CLI runs in the
signed-in user's session, so the two held different kernel objects. Whatever refused the
portable runtime that day, it was not cross-session exclusion, because there was none. The
sentence "the gate serialises all control" is true now and was not true when it was written.
Re-established since, on the corrected gate: with the service running, an elevated
`BladeControl.Cli fan apply` is refused with "another BladeControl host owns the machine-wide
hardware session"; with the service stopped, the same command reaches the device.

Acquiring the lease before touching hardware is the better order. It is deliberately not being
changed here, because it means moving lease acquisition out of `InitializeHost` - the same path
that performs orphaned-Manual crash recovery - and that is not a change worth making late in a
release cycle for a layering improvement with no safety consequence.

---

## Operational

- **PawnIO is required and is not bundled.** It is an external dependency, installed separately,
  and its driver is Authenticode-verified before its readings are trusted. Removing it disables
  closed-loop thermal control; it does not disable the rest of the application.
- **Elevation is required** for service control and installation, as for any Windows service.
- **Recovery from an emergency handoff is deliberate.** The runtime latches and will not resume
  on its own; a person restarts it. This is intentional — an automatic retry after a thermal
  emergency is how you get a loop.
- The MSI installs the service, GUI and diagnostic CLI. The portable archive also carries the
  CLI under `Diagnostics\`; neither package adds it to the machine-wide `PATH`. The shipped
  command surface is not read-only: it includes direct hardware writes and self-tests as well
  as status and diagnostic commands.

---

## Not yet done

- The GPU thermal ladders have never been exercised live. The GPU stayed at or below 48 °C
  throughout every session run here, and manufacturing a thermal emergency to reach them is
  deliberately out of scope. The CPU ladders have been exercised; the GPU ones are tested only
  against synthetic samples.
- **The mid-session performance-mode handoff is still unexercised on hardware.** Triggering it
  needs the mode changed from *outside* a running session — a keyboard shortcut or vendor
  software — which cannot be produced from here, because the ownership gate refuses a second
  writer while the runtime holds the fans. Unit-tested only. That reason is only true as of
  v0.1.4: the gate was session-scoped before then, so the diagnostic CLI could in fact have
  written to the hardware mid-session and produced this. The conclusion was right for the wrong
  reason, and is now right for the stated one.
- The portable zip's UI and runtime executables have been launched, but only on a machine that
  already had the MSI installed. The newly added CLI has had its `--help` path exercised from
  the publish tree, not from an unpacked archive on a clean machine. Nothing here proves any of
  the three applications is free of a dependency the installed product happened to satisfy.
