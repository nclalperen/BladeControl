# BladeControl GUI v0.1

BladeControl GUI v0.1 is a Windows-only .NET 8 WPF application for observing and
controlling an already-running BladeControl Runtime Core. It is a thin IPC client, not a
hardware controller. This document describes the GUI contract against the
hardware-verified Runtime Core V1 surface; it assumes no newer backend capability.

## Architecture and ownership

```text
BladeControl.UI
    -> IRuntimeUiClient
    -> versioned JSON over the local named pipe BladeControl.Runtime.v1
    -> BladeControl Runtime Core
    -> validated telemetry, thermal-control, and Razer hardware layers
```

Production startup always selects `NamedPipeRuntimeUiClient`. The client connects only to
the local machine, uses the current-user pipe option, validates protocol version and
request ID, enforces the 64-KiB response limit, and serializes all exchanges because the
V1 server exposes one pipe instance and handles one request per connection. The explicit
`--design` launch mode may use synthetic data for development preview; production never
falls back to it.

The GUI does not open HID, EC, PawnIO, LibreHardwareMonitor, or NVML; acquire the runtime
ownership semaphore; instantiate `BladeRuntime`; send raw Razer packets; or reproduce
interpolation, hysteresis, rate limiting, watchdog, emergency handoff, or performance
restoration logic. Runtime Core remains the sole hardware owner and validates every
state-changing request again.

The client uses the typed V1 operations for status, doctor, telemetry, performance, fan
state, cursor-based events, built-in curves, performance/fan applies, and thermal-session
start/stop. Diagnostics is read-only and there is no raw, force, unsafe, or debug-console
surface.

The application shell uses a persistent sidebar for Dashboard, Performance, Fans &
Thermal, Monitoring, and Diagnostics. Its near-black/charcoal theme uses restrained green
accents, rounded cards, and distinct good, warning, danger, selected, pending, and disabled
states. The window defaults to 1100 x 720 and resizes down to 900 x 600.

## Connection lifecycle and freshness

The UI is usable when Runtime Core is absent:

- Startup begins in Connecting and attempts a bounded, read-only status request.
- While online, one cancellable polling loop starts a cycle at approximately 500-ms
  cadence. Status and telemetry are refreshed each cycle, events approximately every
  second, and profiles/doctor data approximately every five seconds. Qualification is
  refreshed immediately after connection or reconnection.
- While a thermal session is Running, graphs use the authoritative telemetry embedded in
  runtime status. Otherwise telemetry comes from Runtime Core's diagnostic snapshot
  operation. The UI never acquires sensors itself.
- A transport failure changes the UI to Offline, disables state-changing actions, and
  retains last-known values only with stale/offline presentation. Telemetry more than
  three seconds old is marked stale.
- Offline mode performs one conservative read-only reconnect probe every five seconds.
  Reconnect also permits an immediate manual probe. The single poll gate prevents
  duplicate loops and overlapping status requests.
- Backend rejections and protocol errors are surfaced without pretending that the pipe
  disconnected. A transport error is what moves the connection offline.

State-changing commands are globally limited to one in flight. Buttons are disabled
while that command is pending, backend messages are shown to the user, and a failed
command is never retried automatically.

## Safety gating

The GUI applies these preconditions before sending a request:

| Action | Required Runtime Core state and health |
| --- | --- |
| Apply Balanced, Silent, Custom, Firmware Auto, or Fixed fan targets | Online, no command pending, state exactly `Stopped` |
| Start Dynamic Cooling | Online, no command pending, state exactly `Stopped`, and the latest doctor report says `ThermalOwnershipReady` |
| Stop Dynamic Cooling | Online, no command pending, state `Running`, `Faulted`, or `EmergencyHandoff` |
| Reconnect | Read-only; available while Offline |
| Exit UI | No runtime command is sent |

Runtime Core discovered and enforces the important static-operation rule: performance
and fan-profile applies are rejected unless runtime state is `Stopped`. The UI therefore
does not offer those writes during `Starting`, `Running`, `Stopping`,
`EmergencyHandoff`, or `Faulted`. Thermal readiness in the GUI is only an early gate;
Runtime Core performs the authoritative qualification again before entering Manual mode.
Fault and emergency status, stale sensors, blocked reasons, and backend errors remain
visible rather than being hidden.

## Pages

### Dashboard

The Dashboard is the at-a-glance operating view. It presents only data supplied over IPC:

- CPU package temperature, package power, and utilization when available;
- GPU temperature, power, and utilization when available;
- connection, runtime state, session ID, telemetry freshness/health, scheduler health,
  and thermal-session state;
- Runtime Core's effective fan target, firmware Auto/Manual state, and separately labelled
  firmware-reported fan values;
- current performance mode and CPU/GPU levels.

Quick actions apply Balanced, Silent, or the Custom selection from the Performance page,
and start or stop Dynamic Cooling. Every action uses the shared safety gates. Runtime
fault or emergency-handoff information is promoted to a visible alert.

### Performance

The page shows current and pending policy, supports refresh and restore-from-current, and
offers only the hardware-validated choices:

- modes: Balanced, Silent, and Custom;
- Custom CPU: Low or Medium;
- Custom GPU: Low.

Modeled CPU High/Boost/Overclock and GPU Medium/High values may be visible for context but
are disabled as **Not hardware validated** and cannot become a pending selection. There
is no bypass. Apply is available only while Runtime Core is `Stopped`.

### Fans & Thermal

The page separates three cooling concepts:

- **Firmware Auto** sends the typed Auto fan profile while the runtime is stopped.
- **Fixed** sends explicit Fan 1 and Fan 2 targets from 2000 through 5000 RPM in 100-RPM
  increments. Linking both controls is a UI convenience; both values remain explicit in
  the typed request.
- **Dynamic Curve** starts and stops Runtime Core's closed-loop controller and displays
  session state, active curve, effective target, and telemetry health.

The curve editor loads Runtime Core's immutable `default` CPU/GPU document and supports
local point editing, add/remove, validation, preview, reload, and JSON copy. Each CPU and
GPU curve requires at least two points, temperatures strictly increase and remain above
0 C and below 120 C, RPM never decreases, and dynamic targets are 3000-5000 RPM in
100-RPM increments.

Runtime Core V1 exposes no user-curve save/apply IPC and rejects every start name except
`default`. Accordingly the editor clearly marks Apply unavailable: edits are not saved or
sent, and Start Dynamic Cooling always starts the backend's immutable `default` curve.
See [GUI backend needs](gui-backend-needs.md#2-add-typed-saveselect-support-for-user-thermal-curves).

`0x0D81` is shown only as firmware-reported fan state/value. It is not called Actual RPM,
Physical fan RPM, or Tachometer. Runtime Core's commanded value is labelled Fan Target.

### Monitoring

Monitoring renders lightweight real-time charts from IPC samples only:

- CPU package and GPU temperature;
- effective fan target;
- CPU package and GPU power when supported;
- CPU and GPU utilization when supported.

History is an in-memory fixed-capacity ring sized for approximately two samples per
second and at most 120 seconds. The user can select a 60- or 120-second window and clear
the history. Duplicate or out-of-order timestamps are ignored, unavailable metrics become
gaps, and the page identifies authoritative thermal-session versus diagnostic-snapshot
sources. Nothing is written to disk, no history grows without bound, and offline/stale
data pauses with an explicit label.

### Diagnostics

Diagnostics is a read-only health and provenance surface with these groups:

- **Runtime:** connection, pipe, state, session, active profile, protocol, event count,
  failure, and emergency status. Runtime build version is unavailable in V1.
- **Razer:** HID availability, watchdog zones, zone agreement, known Auto/Manual state,
  and explicitly qualified firmware-reported fan values.
- **Telemetry:** CPU Package health/provenance, GPU/NVML health, deterministic GPU
  selection, selected GPU and PCI ID, LibreHardwareMonitor version, ACPI availability,
  and thermal-ownership readiness.
- **PawnIO:** installed/file version, service state, driver path, Authenticode status,
  Windows trusted signer, embedded and timestamp signers, SHA256, diagnostics, and whether
  provenance is safe for thermal ownership.
- **Scheduler:** requested and actual cadence, execution/lateness, completed cycles,
  overruns, maximum overrun, skipped deadlines, scheduler health, and last acquisition
  duration.

Runtime events are consumed with a sequence cursor, shown newest first, filterable by
kind, and bounded to 500 entries in the UI. Cursor gaps and runtime restarts are indicated;
structured decision, watchdog, exchange, timing, result, and session detail can be
expanded. Copy Diagnostics creates a text snapshot. There is no hardware-write console.

## Notification area behavior

The notification-area menu contains:

- Open BladeControl;
- a read-only Runtime state indicator;
- Start Dynamic Cooling;
- Stop Dynamic Cooling;
- Firmware Auto;
- Exit UI.

The three state-changing tray entries use the same gates and commands as the pages. With
**Close to notification area** enabled (the default), closing the window hides it and the
runtime continues independently. When disabled, closing the window exits the UI. **Exit
UI** always terminates only the GUI process: it does not stop an active thermal session,
return the fans to Auto, or stop Runtime Core.

## UI settings

UI-only preferences are stored as version-1 JSON at:

```text
%LocalAppData%\BladeControl\ui-settings.json
```

The file contains window width/height and maximized state, selected page, close-to-tray
preference, and the 60/120-second monitoring range. Defaults are 1100 x 720, Dashboard,
close to tray enabled, and a 120-second graph window; the window minimum is 900 x 600.
Values are sanitized when loaded, and a missing, corrupt, or unwritable preference file
falls back safely without preventing startup. No performance profile, fan target, thermal
curve, hardware ownership state, or runtime configuration is duplicated in UI storage.

## Known backend limits

GUI v0.1 intentionally degrades safely around five Runtime Core V1 limits: the IPC
contract/endpoint is not yet in a shared hardware-free component, only the immutable
default curve is usable, no approved runtime/service auto-launch path exists, the runtime
does not expose a version/build ID, and firmware fan reports are not proven physical
tachometer data. Exact backend requirements and current UI behavior are recorded in
[GUI backend needs](gui-backend-needs.md).
