# Third-party notices

BladeControl includes and depends on third-party software. This file lists every component,
its licence, and — importantly — whether BladeControl **redistributes** it or merely
**detects and uses** an independently installed copy. The distinction decides which licence
obligations attach to a BladeControl release.

Audited against the `release/v0.1.0` dependency graph. Version numbers are the exact
resolved versions; the build pins them.

---

## 1. Redistributed in the BladeControl installer and portable archive

These ship inside `BladeControl-<version>-win-x64.msi` and the portable zip, because
BladeControl publishes self-contained.

| Component | Version | Licence | Why it is present |
|---|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 0.9.6 | MPL-2.0 | CPU package temperature decoding |
| [BlackSharp.Core](https://github.com/Blacktempel/BlackSharp) | 1.0.7 | MPL-2.0 | Transitive dependency of LibreHardwareMonitorLib |
| [DiskInfoToolkit](https://github.com/Blacktempel/DiskInfoToolkit) | 1.1.2 | MPL-2.0 | Transitive dependency of LibreHardwareMonitorLib |
| [RAMSPDToolkit-NDD](https://github.com/Blacktempel/RAMSPDToolkit) | 1.4.2 | MPL-2.0 | Transitive dependency of LibreHardwareMonitorLib |
| [HidSharp](https://software.seekye.com/hidsharp) | 2.6.4 | Apache-2.0 | Transitive dependency of LibreHardwareMonitorLib |
| Mono.Posix.NETStandard (+ `MonoPosixHelper.dll`, `libMonoPosixHelper.dll`) | 1.0.0 | MIT (Mono project) | Transitive dependency of LibreHardwareMonitorLib |
| .NET 8 runtime and libraries (`System.*`, `Microsoft.Extensions.*`, WPF assemblies, `hostfxr`, `coreclr`, `mscordaccore`, `createdump`) | 8.0.x | MIT | Self-contained publish; the user does not install a runtime by hand |

### Notes on the redistributed set

**MPL-2.0 components.** The Mozilla Public License 2.0 is file-level weak copyleft.
BladeControl uses these as unmodified binary libraries and does not alter their source, so
the obligation is to make the covered source available and to preserve these notices —
satisfied by the upstream links above. MPL-2.0 explicitly permits combining covered software
into a "Larger Work" under a different licence, so it places no licence requirement on
BladeControl's own code.

**Transitive weight is real and larger than it looks.** BladeControl uses
LibreHardwareMonitor for exactly one thing — decoding CPU package temperature — with GPU,
motherboard, controller, storage, network, memory, battery, PSU and power-monitor providers
all disabled. It nevertheless drags in disk, RAM-SPD, HID and Mono-POSIX libraries, because
they are unconditional package dependencies. `MonoPosixHelper.dll` and
`libMonoPosixHelper.dll` are native Unix helpers that ship in the Windows publish and are
inert on Windows. Nothing here is a licence problem; it is recorded so the size and the
attribution list are not mistaken for feature scope.

---

## 2. Detected, never redistributed

### PawnIO — **not bundled** (audited decision)

| Property | Finding |
|---|---|
| Licence | **GPL-2.0-or-later**, with a special linking exception |
| Modules (`PawnIO.Modules`) | LGPL-2.1 |
| Bundled in BladeControl | **No** |
| Installed, downloaded, updated or removed by BladeControl | **No** |
| Upstream | <https://github.com/namazso/PawnIO>, binaries from <https://pawnio.eu> |
| Alternative licensing contact | `admin@namazso.eu` (offered by the author) |

**Exactly how BladeControl reaches PawnIO.** Traced through the code, there are three
touch points and all are indirect or read-only:

1. **Through LibreHardwareMonitor only.** The single API surface BladeControl uses is
   `LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled` and `.Version`
   (`src/BladeControl.Hardware.Windows/Telemetry/LibreHardwareMonitorCpuProvider.cs`,
   `PawnIoProvenance.cs`). BladeControl never opens the PawnIO device, never issues an
   IOCTL, never calls `PawnIOLib`, and never loads a Pawn module.
2. **Read-only registry inspection.** `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO`
   `ImagePath`, read through the 64-bit view, to locate the driver image for provenance
   reporting.
3. **Read-only file and service inspection.** SHA-256 of the driver image, Authenticode /
   Windows-catalog trust verification, and an SCM status query opened with
   `SERVICE_QUERY_STATUS` only.

**Which PawnIO interface is involved.** This is the question the GPL exception turns on.
PawnIO's exception permits combination with "independent modules that communicate with
PawnIO solely through the device IO control interface", and explicitly withholds that
permission from "programs that communicate with PawnIO over the Pawn interface" — anything
loading an `.amx` module into the Pawn virtual machine must itself be GPL-compatible.

BladeControl uses **neither** interface. The IOCTL and Pawn-VM traffic belongs entirely to
LibreHardwareMonitor and `PawnIOLib`, one layer below BladeControl's dependency on managed
LHM types. BladeControl's own relationship to PawnIO is limited to "is it installed, is it
signed, what version" — i.e. inspection of a system component, not use of its programming
interfaces.

**Why bundling is refused for v0.1.5.** Four independent reasons, any one sufficient:

- *Licence entanglement.* Redistributing PawnIO would make BladeControl a distributor of
  GPL-2.0 code, with the corresponding source-availability obligations, and would raise a
  combined-work question that today does not exist. The linking exception is narrower than
  it first appears: the path actually used in practice (LHM → PawnIOLib → Pawn VM modules)
  is the path the exception carves *out*.
- *Signed kernel driver.* PawnIO is a Microsoft-attestation-signed kernel-mode driver. The
  signature, and the trust users place in it, belong to its author. Re-hosting someone
  else's signed driver inside an unsigned third-party installer inverts that trust
  relationship, and would make BladeControl a distribution channel for kernel code it does
  not author, review, or sign.
- *It would weaken a validated safety property.* The runtime verifies PawnIO's provenance —
  Authenticode or catalog trust, publisher subject, file hash — and refuses authoritative
  CPU telemetry, and therefore Manual fan mode, when verification fails
  (`PawnIoProvenanceReader.IsSafeForThermalOwnership`). That check exists precisely because
  the driver arrives from outside BladeControl. Shipping our own copy would turn an
  independent verification into a self-attestation.
- *Not required for the product to work.* Without PawnIO, static performance and fan control
  remain fully available. Only closed-loop thermal control is withheld, and it is withheld
  deliberately and visibly, because the authoritative CPU package temperature it depends on
  is unavailable.

**What the installer does instead.** It searches the PawnIO service registry key — the same
location the runtime's provenance reader uses, so the two cannot disagree — records the
result in the `PAWNIOSTATE` property, and installs regardless. It never downloads or
installs anything. The Diagnostics page reports the full provenance, and the thermal Start
path reports the specific reason when the dependency is missing.

**If bundling is revisited.** The prerequisites are: written clarification from the PawnIO
author (the project offers alternative licensing at the address above) or a legal review
concluding the exception covers the LHM-mediated path; a decision on whether BladeControl's
own licence must become GPL-compatible; a signing story for the installer; and a replacement
for the provenance check that does not reduce to self-attestation.

### NVIDIA Management Library (NVML)

| Property | Finding |
|---|---|
| Provider | NVIDIA display driver |
| Bundled | **No** |
| Loading | `nvml.dll` from `System32` (DCH drivers), with the NVIDIA `NVSMI` directory as a compatibility fallback |
| Entry points declared | Initialise, shutdown, device enumeration and identity, temperature, power, utilisation, clocks, memory |
| Mutation entry points | **None declared or exposed** |

NVML is part of the installed graphics driver and is governed by the NVIDIA driver licence.
BladeControl links no NVIDIA import library and redistributes no NVIDIA binary.
Reference: <https://docs.nvidia.com/deploy/nvml-api/>

### Windows platform APIs

BladeControl uses documented Win32 APIs (HID, SetupAPI, named pipes, Service Control
Manager, WinTrust/WinVerifyTrust, `advapi32` security primitives) through P/Invoke. These
are operating-system components, redistributed by nobody.

---

## 3. Build and test only — never redistributed

Present in the repository and the CI pipeline, absent from every shipped artifact.

| Component | Version | Licence | Role |
|---|---|---|---|
| MSTest.TestFramework / MSTest.TestAdapter | 3.1.1 | MIT | Test framework |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT | Test host |
| System.CodeDom | 10.0.2 | MIT | Transitive (build graph) |
| System.Management | 10.0.2 | MIT | Transitive (build graph) |
| System.IO.Ports (+ non-Windows runtime packs) | 10.0.3 | MIT | Transitive (build graph) |
| System.Threading.AccessControl | 10.0.3 | MIT | Transitive (build graph) |
| [WiX Toolset](https://wixtoolset.org/) | 5.0.2 | MS-RL | Builds the MSI; no WiX code ships inside it |

`build/pack.ps1` fails the build if any test-framework assembly appears in the publish
output, so this separation is enforced rather than assumed.

---

## 4. Trademarks

BladeControl is an independent, unofficial project. It is **not** produced, endorsed,
sponsored or supported by Razer Inc. "Razer" and "Razer Blade" are trademarks of Razer Inc.
and are used solely to identify the hardware this software targets. "Windows" is a trademark
of Microsoft Corporation. "NVIDIA" is a trademark of NVIDIA Corporation.

---

## 5. BladeControl's own licence

**GNU General Public License v3.0.** See [LICENSE](LICENSE) for the full text, and
[docs/license-recommendation.md](docs/license-recommendation.md) for the analysis this audit fed
and why the copyright holder chose copyleft over the Apache-2.0 that was recommended there.

The licence is conveyed with the binaries as GPL-3.0 requires: the MSI installs `LICENSE.txt`,
the portable zip carries it, and the installer's licence dialog presents the GPL text.
