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
  plus one cycle execution — about 1.1 s.** For the three-sample emergency ladders (1.5 s
  minimum by construction) this is comfortable. For the single-sample immediate handoffs — CPU
  100 °C, GPU 79 °C — it means up to about a second of delay against, in the GPU case, 1 °C of
  margin below the hardware shutdown point.
- Firmware slowdown (77 °C) and hardware shutdown (80 °C) remain in force throughout and are not
  BladeControl's to defeat. They are the backstop; the ladder above is the first line, not the
  only one.

**This is accepted for v0.1.0 and documented rather than fixed.** Removing it means separating
acquisition from actuation across threads, which introduces sole-HID-ownership, sample-freshness
and preemption invariants that deserve their own design pass rather than being bolted onto a
latency patch. The per-component statistics exist precisely so that decision can be made on
distribution data.

Telemetry acquisition is **variable, not constant**: observed between roughly 160 ms and 400 ms
across sessions on the same machine. Any future redesign should rest on a distribution, not on a
single snapshot.

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

## Crash recovery leaves a window where nothing owns the fans

A hard-killed runtime — `TerminateProcess`, a power event, a service crash — runs no managed
cleanup, so the fans stay at the last speed the session commanded, in Balanced + Manual, with
nothing driving them. The Service Control Manager restarts the service, and host initialisation
performs a one-time recovery back to firmware Auto before it will serve anything.

Measured on the reference machine by killing the service process mid-session:

| Step | Observed |
|---|---|
| Service process killed | fans held at last commanded speed, Manual |
| SCM restart | **~25 s** (configured `RESTART -- Delay = 20000 ms`) |
| Firmware state after restart | **Balanced + Auto** — recovery succeeded |

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

This is a layering flaw, not a safety hole. The refused host performs no writes, the gate still
serialises all control, and the refusal message is accurate and actionable ("Stop the
'BladeControl Runtime' service before running a console host"). Observed directly: the portable
runtime, started while the installed service was live, got as far as both hardware sessions and
then exited at the gate.

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
- The MSI installs the service and the GUI. The diagnostic CLI is currently built from source
  and not shipped, so field diagnosis of an installed machine needs the repository.

---

## Not yet done

- The GPU thermal ladders have never been exercised live. The GPU stayed at or below 48 °C
  throughout every session run here, and manufacturing a thermal emergency to reach them is
  deliberately out of scope. The CPU ladders have been exercised; the GPU ones are tested only
  against synthetic samples.
- Behaviour across a reboot has not been exercised: the service is `AUTO_START`, but a cold boot
  into a session has not been observed end to end.
- The portable zip has been unpacked and both executables launched, but only on a machine that
  already had the MSI installed. It has not been run on a clean machine, so nothing here proves
  it is free of a dependency the installed product happened to satisfy.
