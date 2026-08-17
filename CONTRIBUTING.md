# Contributing to BladeControl

BladeControl drives cooling hardware on a laptop. A bug here can mean sustained high
temperatures, not a wrong pixel. That single fact shapes every rule below.

Note before you start: **no open-source licence has been applied yet**
([why](docs/license-recommendation.md)). Until one is, the project cannot properly accept
contributions of copyrighted work. Issues, hardware reports and review comments are welcome now;
please hold code contributions until a licence lands, or open an issue to discuss.

## Ground rules

### The runtime is the only thing that touches hardware

`BladeControl.Service` hosts the runtime and owns Razer HID, the embedded controller and the
telemetry providers. Everything else asks it over typed IPC.

The user interface must never reference `BladeControl.Hardware.Windows` or
`BladeControl.Service`, construct a `BladeRuntime`, take the hardware semaphore, or send raw
Razer commands. This is enforced by a test, not by trust. If you find yourself needing hardware
access from the GUI, the answer is a new IPC operation.

### Do not change validated hardware semantics casually

These were established against the reference platform and verified on real hardware:

- Razer command IDs, SET ordering, fan verification
- Thermal curve interpolation, hysteresis, rate limiting
- Scheduler semantics, emergency Auto behaviour, watchdog
- Safe shutdown and firmware restoration

A change to any of them needs a stated reason, a test, and — before release — hardware
re-validation. "It looked wrong" is not sufficient; the reference platform's firmware is the
authority, not intuition about how it ought to behave.

### Safety gates are not obstacles

The runtime refuses Manual fan mode without a fresh authoritative qualification. It refuses
thermal control when PawnIO provenance cannot be verified. It hands back to firmware Auto on
fault. Making a gate looser to get a feature working is the one change most likely to be
rejected outright. If a gate is wrong, argue that it is wrong.

### No hardware operations in tests

The suite is hardware-free and must stay that way: it runs in CI on machines with no Razer
hardware. No test may perform a fan write, a performance apply, a HID exchange, install a
service, or install the MSI. Use the existing fakes and test doubles.

## Working on the code

Requires the .NET 8 SDK on Windows x64. For installer work, `dotnet tool install --global wix
--version 5.0.2`.

```bash
dotnet test BladeControl.sln -c Release
dotnet build BladeControl.sln -c Release --no-restore
dotnet format BladeControl.sln --verify-no-changes
```

All three must pass with zero failures, zero warnings and no formatting diff. Warnings are
errors across the solution; this is not negotiable in a pull request.

For release artifacts: `powershell -ExecutionPolicy Bypass -File build/pack.ps1`.

### Style

Match the surrounding code — it is consistent, and consistency beats preference.

Comments earn their place by explaining *why*, especially where the reason is not
reconstructible from the code: a firmware quirk, an ordering constraint, a rejected
alternative. Do not narrate what the next line does.

### Versioning

The product version lives in `Directory.Build.props` and nowhere else. If you find yourself
editing a version string in a second file, that is a bug in the build, not something to
duplicate.

## Pull requests

- One concern per pull request. A packaging change and a protocol change do not belong together.
- Say what you verified and how. If you tested on hardware, say which model and what you
  observed. If you did not, say that — it is useful information, not an admission.
- Include tests for behaviour you can test without hardware.
- Update the docs your change makes stale, and add a `CHANGELOG.md` entry under `[Unreleased]`.
- Never commit secrets, signing certificates or private keys. Signing credentials belong in the
  release environment, never in the repository — see [docs/code-signing.md](docs/code-signing.md).

## Reporting hardware findings

Reports from models other than the RZ09-0483 reference platform are genuinely valuable, and
currently the main way BladeControl's compatibility picture can improve. Useful reports include
the exact model string, BIOS/EC version, what the Diagnostics page shows (Copy Diagnostics
produces a text snapshot), and what you expected versus what happened.

Please do not send fan-control experiments performed by patching out safety gates. A report that
"it works if I remove the qualification check" tells us that the check does its job.

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).
