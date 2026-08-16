# GUI backend needs

This document records backend capabilities that BladeControl GUI v0.1 cannot safely
provide against the hardware-verified Runtime Core V1 IPC surface. The GUI must keep the
affected controls unavailable or clearly limited; none of these gaps may be filled with
direct hardware access, direct runtime configuration-file writes, service-shell commands,
or a second hardware owner.

## 1. Extract the shared IPC contract and endpoint

### Current V1 constraint

- Request/response envelopes, operation names, most payloads, DTOs, protocol version, and
  message limits live in `BladeControl.Runtime` beside the runtime engine.
- The canonical pipe name (`BladeControl.Runtime.v1`) and the existing named-pipe client
  live in `BladeControl.Service`, which the GUI must not reference because that project is
  also the Windows hardware host.
- The doctor response is currently created as an anonymous service-host object, so the GUI
  has to maintain a tolerant mirror of that wire shape.
- Consequently the GUI references `BladeControl.Runtime` for wire types, implements its
  own thin pipe transport, and duplicates the pipe-name constant.

### Backend need

Extract a hardware-free IPC contracts component referenced by Runtime, Service, CLI, and
UI. It should be the single owner of:

- the local pipe endpoint name;
- protocol version and message/event-batch limits;
- request and response envelopes and operation identifiers;
- every typed request and response DTO, including the doctor report and scheduler DTOs;
- the JSON enum/naming compatibility rules needed by both ends of the pipe.

The component must not reference the runtime engine, service host, Razer/HID, telemetry
providers, PawnIO, NVML, or Windows hardware assemblies. The existing V1 wire format can
remain compatible while ownership of these definitions moves. Completion means the GUI no
longer duplicates an endpoint or wire DTO and does not need a project reference to an
engine or hardware-host assembly merely to speak IPC.

## 2. Add typed save/select support for user thermal curves

### Current V1 constraint

Runtime Core has an internal `RuntimeConfigurationStore` capable of validating and
atomically storing user-curve documents, but that store is not connected to the runtime
host's IPC surface. IPC currently behaves as follows:

- `ListBuiltInCurves` returns only `default`;
- `GetThermalCurve` accepts only `default`;
- `StartThermalControl` rejects every name except `default`;
- there is no save/update operation and no operation that applies edited curve points.

The compiled `default` curve is intentionally immutable. Therefore GUI v0.1 loads it into
the editor, validates edits locally, previews them, and can copy runtime-shaped JSON, but
it never presents those edits as saved or active. Starting Dynamic Cooling always asks
Runtime Core to start its built-in `default` curve.

### Backend need

Expose a typed, versioned user-curve workflow owned entirely by Runtime Core. At minimum it
needs:

- list/get operations that distinguish immutable built-ins from mutable user curves;
- a save or update operation returning structured runtime validation results;
- a defined way to select a saved curve for `StartThermalControl`, or a separate typed
  apply/select operation with equivalent semantics;
- runtime-side enforcement of point count, strictly increasing temperatures,
  non-decreasing RPM, 100-RPM quantization, the 3000-5000 RPM dynamic range, safe naming,
  and atomic persistence.

Applying a curve must retain Runtime Core's existing lifecycle and ownership checks. The
GUI must never write files in the runtime configuration directory or send an unvalidated
raw controller definition directly to hardware.

## 3. Provide an approved runtime/service launch path, if desired

### Current V1 constraint

No BladeControl binary exposes service install, start, stop, delete, or runtime-host
auto-launch as an application operation. A named-pipe request also cannot start a server
that is not running. The V1 pipe is restricted to the creating user and, on Windows, the
matching elevation context.

GUI v0.1 consequently launches in Offline mode, offers a read-only reconnect action, and
probes conservatively for an already-running Runtime Core. It does not invoke `sc.exe`,
spawn the CLI console host, request elevation, or silently create a second runtime owner.
UI preferences under `%LocalAppData%` are not a substitute for runtime/service
configuration.

### Backend/deployment need

If "Start Runtime Core" or automatic startup is required, provide an explicit,
installation-supported lifecycle design outside the hardware client. For example, a
properly installed Windows service plus a narrowly scoped, elevation-aware launcher or
broker could return structured start state and errors. The design must define service
identity, elevation, pipe accessibility, idempotency, and single-owner behavior. It must
not give the GUI a generic shell or arbitrary service-control surface.

Until such a path exists, Offline plus Reconnect is the complete and safe GUI behavior.

## 4. Expose runtime version and build identity

### Current V1 constraint

The IPC response envelope identifies protocol version, and `RuntimeStatusDto` identifies
runtime state and session, but neither reports the running host's product version, build
identifier, source revision, or process-instance/event epoch. The thermal session ID is
not a stable identity for the host or its event sequence. Protocol version `1` is not a
runtime build version. The GUI therefore displays Runtime version as unavailable, must not
infer it from the UI's locally referenced assemblies, and can only detect an event-stream
restart when the server's latest retained sequence visibly moves behind its cursor.

### Backend need

Add a typed runtime-info response, either as a dedicated read-only operation or as stable
fields in status. It should report at least:

- Runtime Core product/semantic version;
- an immutable build ID or source revision;
- a runtime process-instance ID or event epoch that changes whenever event numbering
  restarts;
- the supported IPC protocol version.

The values must originate from the server process so Diagnostics can identify the host
that actually answered the pipe and can report UI/runtime mismatches accurately.

## 5. Preserve firmware fan-report semantics; add tach only if validated

### Current V1 constraint

The fan values returned by `GetFanState` and related firmware snapshots ultimately come
from the Razer `0x0D81` report and are modeled internally as `FirmwareReportedRpm`. This
field has not been proven to be a physical fan tachometer. It may represent firmware
target/state rather than measured rotor speed.

GUI v0.1 therefore uses the labels **Firmware-reported fan state/value**. It uses **Fan
Target** for Runtime Core's commanded effective target. It never labels `0x0D81` as
Actual RPM, Physical fan RPM, or Tachometer.

### Backend need

Make the wire contract unambiguous in a compatible revision: rename the fields or attach
source/authority metadata that states they are firmware-reported values. If physical fan
speed is later required, first validate a real tachometer source and expose it as a
separate typed metric with timestamp, availability, health, and provenance. Do not
reinterpret the existing V1 values as physical measurements without that validation.
