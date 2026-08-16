# Third-party dependencies

## LibreHardwareMonitorLib

- Package: `LibreHardwareMonitorLib`
- Version: `0.9.6` (exactly pinned)
- License: MPL-2.0; upstream also publishes third-party notices for portions of the project
- Purpose in BladeControl: CPU telemetry decoding only
- Scope: referenced only by `BladeControl.Hardware.Windows`

BladeControl configures LibreHardwareMonitor with CPU monitoring enabled and GPU,
motherboard, controller, storage, network, memory, battery, PSU, and power-monitor
providers disabled. NVIDIA GPU telemetry is obtained independently through NVML.
BladeControl does not use LibreHardwareMonitor for fan control.

Upstream: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

## PawnIO

- Classification: external optional system dependency
- Bundled: no
- Installed, downloaded, removed, or modified by BladeControl: no
- Purpose: read-only CPU MSR access used internally by LibreHardwareMonitor to decode
  CPU Package temperature and related CPU sensors

When PawnIO is absent, static Razer performance and fan functionality remains
available. Thermal closed-loop control refuses to enter Manual fan mode because
the required authoritative CPU Package temperature is unavailable.

BladeControl exposes no PawnIO handle, IOCTL, arbitrary MSR, EC, PCI, or physical
memory API.

## NVIDIA Management Library (NVML)

- Provider: NVIDIA graphics driver
- Package or binary redistributed by BladeControl: none
- Purpose: read-only NVIDIA GPU enumeration and telemetry
- Loading: `nvml.dll` from Windows System32 (DCH drivers), with the standard
  NVIDIA `NVSMI` driver directory as a compatibility location

Only initialization, shutdown, device enumeration/identity, temperature, power,
utilization, clock, and memory query entry points are declared. No NVML mutation
entry point is declared or exposed.

Reference: https://docs.nvidia.com/deploy/nvml-api/
