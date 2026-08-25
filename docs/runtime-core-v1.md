# Runtime Core V1

Runtime Core V1 turns the hardware-verified thermal controller into one long-running
hardware owner. The control loop consumes `ThermalTelemetrySample` through
`IControlTelemetryProvider`; that interface has no Razer dependency and cannot perform
HID requests. Full `TelemetrySnapshot` acquisition remains an explicit diagnostic path.

The scheduler uses monotonic absolute deadlines at 500 ms. Work time is subtracted from
the wait to the next deadline. An overrun never creates another concurrent cycle: the
next cycle starts immediately and the lateness/overrun metrics are recorded. The Razer
watchdog reads only `0x0D82` for zones 1 and 2 every five seconds.

The runtime publishes typed, bounded events and observes every exchange at the single
`RazerClient` boundary. Its named pipe accepts strict, versioned JSON requests for the
documented typed operations only. The pipe uses `PipeOptions.CurrentUserOnly`, one server
instance, a 64-KiB message limit, and a fail-closed client-computer check that rejects
remote named-pipe connections. There is no network/HTTP listener and no raw HID,
packet, PawnIO, NVML, file-command, shell, or arbitrary execution operation.

## Development console host

```powershell
dotnet run --project .\src\BladeControl.Cli\BladeControl.Cli.csproj -- service console [--verbose]
```

The console host and Windows Service path share the same shutdown sequence: stop new
cycles, establish and verify firmware Auto, restore captured performance state, close
telemetry, and release ownership. (Written as "Balanced + Auto" originally; taking fan
ownership stopped moving the machine to Balanced in v0.1.1, so the mode restored is the one
the session began in.)

`runtime status`, `runtime doctor`, and `service console` accept `--verbose`. The option
changes diagnostics/rendering only and never changes hardware behavior. Runtime doctor
performs a fresh authoritative sensor qualification; thermal ownership is ready only
when PawnIO provenance, CPU Package temperature, NVML GPU temperature, deterministic GPU
selection, and Razer HID availability all pass. `StartThermalControl` repeats this
qualification immediately before any Manual-mode operation and does not trust an older
doctor result.

PawnIO diagnostics distinguish Windows catalog/file trust from the signer embedded in
the PE image. A WHCP catalog signer and a different embedded signer can therefore both
be reported without ambiguity. Thermal ownership uses the Windows trust result; the
embedded signer is diagnostic and cannot make an untrusted driver safe.

## Thermal IPC client

`thermal run --curve default [--verbose]` is an IPC client for the already-running
`BladeControl.Runtime.v1` host. It never opens Razer HID, PawnIO, NVML, or the named
hardware-ownership semaphore and never falls back to a standalone controller. If the
host is unavailable, the command tells the user to start `BladeControl.Cli service
console` and exits.

The client sends one typed `StartThermalControl` request, then follows cursor-based event
batches from the runtime's bounded event log. Verbose rendering includes structured
telemetry, decisions, watchdog checks, ownership/emergency events, and complete protocol
exchange reports. Ctrl+C sends exactly one typed `StopThermalControl` request and waits
for the runtime's safe Auto handoff and performance restoration. After that response,
the client drains bounded 16-event batches for up to five seconds, stopping only when
the target `SessionStopped` event (or stopped Runtime state) is observed and the cursor
has caught the retained log. It never sends a second stop request. Retention gaps,
cursor resets, session transitions, timeouts, and disconnects are reported explicitly;
the client does not accumulate an unbounded event history.

The completion summary uses only the already-returned Runtime status and streamed IPC
events. When Runtime state is `Stopped`, telemetry health, watchdog mode, and scheduler
statistics are labeled as last-session history rather than current hardware state. No
extra hardware read is performed for presentation. Stopping a thermal session does not
stop the service host; Ctrl+C in the service-console terminal remains the separate
host-shutdown action.

## Windows Service (manual, later operation only)

Runtime Core V1 does not install or mutate a service. After publishing the service
executable, an administrator may later install it explicitly. Because the V1 pipe is
restricted to the creating identity and, on Windows, the same elevation level, configure
the service and its administrative GUI/CLI client to run under the same dedicated local
account and elevation context.

Example commands for a later, explicit administrative deployment (do not run during
preflight):

```powershell
sc.exe create BladeControlRuntime binPath= '"C:\Program Files\BladeControl\BladeControl.Service.exe" --service' start= auto
sc.exe description BladeControlRuntime "BladeControl Runtime Core V1"
```

Before starting the service, set its Log On identity through an approved secure Windows
service-management workflow; do not put an account password in repository scripts. After
the identity is configured, an administrator may explicitly run:

```powershell
sc.exe start BladeControlRuntime
```

Removal is likewise a separate explicit administrative action:

```powershell
sc.exe stop BladeControlRuntime
sc.exe delete BladeControlRuntime
```

No install, start, stop, or delete API exists in the BladeControl binaries.

## Configuration

Runtime preferences, user curves, and user profiles use small version-1 JSON documents.
Writes use a same-directory temporary file, flush-to-disk, and atomic replacement. The
built-in default curve is generated from the immutable compiled definition and is never
overwritten. Invalid configuration is rejected before Manual mode can be entered.
