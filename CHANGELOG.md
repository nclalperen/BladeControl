# Changelog

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
BladeControl follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); while below
1.0.0 the protocol and IPC contract may change in a minor release.

## [Unreleased]

Nothing yet.

## [0.1.0] — unreleased

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

[Unreleased]: https://github.com/nclalperen/BladeControl/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/nclalperen/BladeControl/releases/tag/v0.1.0
