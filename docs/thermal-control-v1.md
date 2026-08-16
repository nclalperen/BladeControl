# Thermal Control V1

Thermal Control V1 is a conservative BladeControl policy, not a reconstruction
or claim about the Razer factory fan curve.

## Sensor authority

The required CPU safety sensor is the unique LibreHardwareMonitor sensor whose
hardware type is `Cpu`, sensor type is `Temperature`, and name is exactly
`CPU Package`. The provider must have a working PawnIO backend. The required GPU
safety sensor is the selected physical NVIDIA GPU's NVML core temperature.

Both samples must be present, valid, finite, within `0 C < temperature < 120 C`,
and no older than two seconds. ACPI thermal zones are diagnostic only and never
satisfy the CPU Package requirement.

Razer command `0x0D81` is described as firmware-reported fan RPM/state. It is
not treated as an authoritative physical tachometer because hardware testing
showed that it can retain a previously set value after returning to Auto mode.

## Default policy

CPU curve:

| Temperature | Target |
|---:|---:|
| 50 C | 3000 RPM |
| 60 C | 3300 RPM |
| 70 C | 3800 RPM |
| 80 C | 4400 RPM |
| 88 C | 5000 RPM |

GPU curve:

| Temperature | Target |
|---:|---:|
| 45 C | 3000 RPM |
| 55 C | 3300 RPM |
| 65 C | 3800 RPM |
| 72 C | 4400 RPM |
| 78 C | 5000 RPM |

Curves are linearly interpolated and always quantized upward to the next 100 RPM.
Both curves are evaluated and the higher demand is applied equally to both fans.
Thermal Control V1 curves are restricted to 3000..5000 RPM in 100-RPM steps.

Fan increases are immediate at the decision layer. A decrease requires at least
3 C cooling relative to the condition that caused the current target and three
consecutive lower-demand samples. Decreases are limited to 300 RPM per second.
Normal writes are coalesced when the target is unchanged and are separated by at
least one second.

## Runtime state machine

Before any write, `thermal run` validates both authoritative temperatures,
captures the complete non-telemetry firmware state, requires consistent Auto fan
mode, and confirms that the original performance profile is within the
hardware-validated restoration policy.

It then enters Balanced + Manual and establishes 3000 RPM on both fans. On normal
stop, it first establishes and verifies Balanced + Auto, then restores the
captured performance profile. Original performance is never restored while
Manual fan mode is active.

CPU Package temperature at or above 90 C, GPU temperature at or above 80 C,
stale required data, repeated missing/invalid samples, repeated provider failure,
or an internal/controller failure causes exactly one emergency attempt to return
to Balanced + Auto. The loop stops and cannot automatically re-enter Manual.
Performance restoration is attempted once only after Auto is verified. There is
no SET retry.

Software cleanup cannot guarantee recovery after abrupt power loss, kernel
bugcheck, forced process termination, or total operating-system failure. Firmware
initialization after reboot is outside BladeControl's scope.

## Simulation and acceptance

`thermal simulate` parses files without opening Razer HID, NVML, PawnIO, or any
other hardware provider. CSV traces use:

```text
timestamp,cpu_temp,gpu_temp
2026-08-16T12:00:00Z,55,48
```

`thermal selftest --verbose` is the only end-to-end acceptance path. It requires
the exact verified initial state (Custom + Auto, CPU Medium, GPU Low), qualifies
ten real telemetry samples, uses a current-temperature-centered curve limited to
3200..3900 RPM, and injects a software-only missing CPU sample to exercise the
production emergency handoff. The selftest is never run automatically.

## Security boundary

The existing Razer transport remains limited to typed protocol factories and the
hardware-verified performance/fan commands. Thermal Control V1 adds no Razer
command, raw packet API, output report, `DeviceIoControl`, EC access, driver
operation, retry, unsafe switch, or sensor-bypass option.
