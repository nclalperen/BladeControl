# Changelog

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
BladeControl follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); while below
1.0.0 the protocol and IPC contract may change in a minor release.

## [Unreleased]

Nothing yet.

## [0.1.3] — 2026-08-24

Bug fixes found by reading the shipped 0.1.1 interface against the hardware it was
describing.

### Fixed

- **Telemetry was labelled "Live" while no session was running.** The dashboard reached its
  live branch whenever a sample was fresh and the state was not Stopped, so a machine with
  firmware owning the fans showed "Live — 1 s old" beside a runtime state of "Stopped". The UI
  polls the provider while idle, so the sample really was fresh — but freshness and ownership
  are different claims. Idle states now read "Monitoring", matching the wording the compact
  panel already used, and the age is still reported.
- **The same defect in the compact panel, for the other states.** It special-cased Stopped
  alone, leaving Faulted and EmergencyHandoff claiming live telemetry after cooling had gone
  back to firmware. Both surfaces now share one rule: only Running is live.
- **ScrollBar and CheckBox were never themed**, so WPF drew system chrome — a near-white
  scrollbar on every scrolling page, and white box glyphs — against surfaces around `#131815`.
  Both are now templated. `SubtleCheckBoxStyle` set only the foreground, which left the box
  itself system-drawn; it now extends the themed style instead of replacing it.
- **Diagnostics reported "GPU power: Yes" for a value that never arrives.** The capability
  flag tested whether the driver had declined with `NotSupported`, while its CPU counterpart
  tested whether a reading was actually valid. NVML returns a generic failure here, which is
  not `NotSupported`, so the flag stayed Yes. It now means what the CPU one means: a reading
  is available.

### Known

- **GPU power and utilization do not reach the service.** NVML answers the LocalSystem
  service's temperature and clock queries and refuses power and utilization with
  `NVML_ERROR_UNKNOWN`, while the same provider code called from an interactive process reads
  both. Documented with the measurements in `docs/known-limitations.md`. No control path
  depends on either metric.

## [0.1.1] — 2026-08-24

First tagged and released version. The 0.1.0 milestone below reached feature-complete but was
never independently tagged; this release carries its packaging work forward plus the material
changes made while settling the open questions that milestone left behind.

### Added

- **Visual identity.** Application icon, a state-aware system tray icon (idle / warning /
  emergency, reusing the existing runtime-state-to-tone mapping), and README and GitHub
  social-preview artwork.

### Changed

- **GPU thermal qualification is mode-dependent, not fixed to one signature.** Dynamic Cooling
  qualifies against the limits of whichever performance mode the machine is already in
  (Balanced 87/89/92 °C, Silent/Custom 75/77/80 °C), anchored to the driver's current thermal
  target rather than to a value baked into the GPU signature. An anchor not observed on the
  qualified part is refused even when it looks plausible.
- **Performance mode and fan ownership are orthogonal.** Taking or releasing fan control no
  longer forces the machine to Balanced; Silent stays Silent, Custom stays Custom throughout a
  session.
- **Changing performance mode during a running session now ends it.** The limits in force were
  derived for the mode the session qualified in, so a mode change underneath it hands the fans
  back to firmware rather than continuing to control against stale limits.
- **CPU and GPU performance levels are fully selectable** (CPU Low/Medium/High/Boost, GPU
  Low/Medium/High) in Custom mode. Overclock remains excluded from both, deliberately, so
  BladeControl cannot interfere with tuning done in XTU.
- **UI**: single consolidated fan control replacing the earlier split controls, a redesigned
  slider, and telemetry charts added to the Dashboard, Fans & Thermal, and Performance pages.
  The emergency-handoff banner's tone and wording were corrected to present a completed handoff
  as protection working, not as an alarm.

### Licence

- **`GPL-3.0-or-later`** declared once, in `Directory.Build.props`, as
  `PackageLicenseExpression`, so every built assembly carries it.

### Validated

- **Cold-boot recovery on real hardware.** A genuine reboot, unaided delayed auto-start, the
  machine returning in its pre-reboot performance mode, qualification against that mode's own
  limits, a full session, and a clean stop restoring the booted state.

## 0.1.0 — unreleased (never tagged; superseded by 0.1.1)

First release engineered as installable Windows software. The controller, runtime, compact
panel and advanced application were hardware validated on the reference platform before this
work; everything below is packaging, hosting and distribution.

### Added

- **Windows Installer package.** `BladeControl-0.1.0-win-x64.msi`, built with WiX 5. One
  elevation prompt installs to `%ProgramFiles%\BladeControl\`, registers the
  **BladeControl Runtime** service, creates a Start Menu shortcut and normal Installed Apps
  metadata, and configures sign-in launch. Major-upgrade and clean-uninstall semantics.
- **Windows service host.** The runtime now runs under the SCM as `BladeControl.Runtime`
  (display name *BladeControl Runtime*) as LocalSystem, using the supported
  `Microsoft.Extensions.Hosting.WindowsServices` lifetime. Delayed automatic start, restart
  recovery on unexpected process failure, and a clean SCM stop that runs the existing safe
  shutdown path.
- **Named-pipe access control.** Explicit DACL granting locally logged-on users read/write,
  denying network and anonymous callers, and withholding pipe-instance creation and
  descriptor rewriting from non-administrators. The client verifies the pipe's owner is
  privileged, so an unprivileged process cannot impersonate the runtime. Threat model in
  `docs/ipc-security.md`.
- **Runtime host singleton.** A machine-wide gate taken before any device is opened, so a
  developer console host and the installed service can no longer both own the hardware.
- **Sign-in launch.** `Start BladeControl with Windows`, default on, via the per-user Run key —
  no elevation to change, and disableable from Task Manager's Startup tab. An existing
  registration is re-pointed after an upgrade relocates the binaries.
- **Service readiness presentation.** While the runtime has never answered and the startup
  grace window is open, the interface shows *Connecting to BladeControl Runtime…* instead of a
  hard offline fault — the expected state at sign-in, since the service starts delayed. Command
  gating is unaffected; the existing 5-second reconnect probe keeps its cadence.
- **Portable archive** `BladeControl-0.1.0-win-x64-portable.zip`, documented as not installing
  or starting the service and intended for developers.
- **Symbols archive** and `SHA256SUMS.txt` published with every build.
- **Third-party audit** (`THIRD-PARTY-NOTICES.md`) recording every dependency, its licence, and
  whether BladeControl redistributes it.
- **Repository documentation**: README, SECURITY, CONTRIBUTING, this changelog, IPC security
  model, code-signing plan, installer test checklist, licence recommendation.
- **CI and release workflows.** Every push and pull request runs restore, Release tests,
  Release build and a formatting check. A version tag additionally publishes x64 binaries,
  builds the installer, and produces release assets with hashes — and refuses to publish if any
  gate fails.
- **Release-engineering tests** covering service host mode selection, safe shutdown on stop,
  the pipe ACL policy, reconnect during service startup, startup-registration enable/disable,
  install/config path separation, version consistency, absence of a GUI hardware path, and
  absence of a duplicate runtime ownership path.

### Changed

- **One authoritative version.** `Directory.Build.props` holds the product version; assemblies,
  the interface, the service and the installer all derive it. No version string is maintained
  in more than one place.
- **Design system normalised** into named spacing, corner-radius, compact-density and
  chart-colour tokens, with `TelemetryChart` resolving its palette from the theme rather than
  private constants. Verified pixel-identical on the compact surface.
- **Publish layout** is self-contained x64, deliberately not single-file and not trimmed:
  single-file self-extraction is a poor fit for a service that must run before first sign-in,
  and WPF resource loading plus reflective sensor discovery are what trimming breaks.
- **User settings** remain in `%LocalAppData%\BladeControl\ui-settings.json`, never under
  Program Files, and survive upgrade and uninstall.

### Removed

- The hand-rolled `StartServiceCtrlDispatcher` P/Invoke service host, replaced by the supported
  .NET hosting lifetime.
- `PipeOptions.CurrentUserOnly` on both ends of the IPC channel — meaningless once the server
  runs as LocalSystem, and replaced by an explicit ACL plus server-owner verification rather
  than by a weaker restriction.

### Licence

- **GNU General Public License v3.0.** Applied to the project and conveyed with the binaries:
  the MSI installs `LICENSE.txt`, the portable zip carries it, and the installer's licence
  dialog presents the GPL text. Reasoning in `docs/license-recommendation.md`.

### Not included

- **PawnIO is not bundled.** Detected and reported only. It is GPL-2.0 licensed with a linking
  exception that does not cover the path actually used, it is a third-party signed kernel
  driver, and shipping our own copy would turn an independent provenance check into a
  self-attestation. Reasoning and reconsideration prerequisites in `THIRD-PARTY-NOTICES.md`.
- **No Authenticode signature.** Pre-release builds are unsigned; verify the published SHA-256.
  Where signing belongs in a public pipeline is documented in `docs/code-signing.md`.
- **No validated hardware beyond one machine.** Everything here was exercised on a single
  Razer Blade 16 (RZ09-0483). Thermal-limit qualification is by exact GPU identity and thermal
  signature and fails closed on anything else, so other machines are refused rather than
  guessed at.
- **The GPU thermal ladders have never run live.** The GPU stayed at or below 48 C throughout,
  and manufacturing a thermal emergency to reach them is deliberately out of scope.

[Unreleased]: https://github.com/nclalperen/BladeControl/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/nclalperen/BladeControl/releases/tag/v0.1.3
[0.1.1]: https://github.com/nclalperen/BladeControl/releases/tag/v0.1.1
