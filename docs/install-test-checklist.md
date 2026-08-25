# Installer validation checklist

Manual procedure for validating a BladeControl MSI.

**Status: executed on the reference machine.** Install, in-place upgrade, uninstall and
reinstall have all been run against a real Razer Blade 16 (RZ09-0483), with the service
registered, started, and exercised through live thermal sessions. Firmware was left in fan mode
Auto after uninstall, and user settings survived it. Results are recorded in
[engineering-log.md](engineering-log.md).

Two things below remain unexercised: a cold-boot autostart, and a run on a clean machine that
has never had BladeControl installed. Both are called out in
[known-limitations.md](known-limitations.md).

## Where to run this

**Steps 1–9 (packaging and lifecycle): a disposable Windows 11 x64 VM.** A VM has no Razer
hardware, so the runtime will initialise, find no controller, and report that. That is enough to
validate installation, service registration, autostart, sign-in launch, upgrade and uninstall
without risking hardware.

**Step 10 (hardware acceptance): the RZ09-0483 reference machine, and only after 1–9 pass.**
This is the first time the installed product drives real hardware.

Take a VM snapshot before starting. Several steps are only meaningful from a clean state.

## Preparation

```powershell
Get-FileHash -Algorithm SHA256 .\BladeControl-0.1.4-win-x64.msi
# compare against SHA256SUMS.txt
```

Record the machine state before installing, so "clean removal" can be checked rather than
assumed:

```powershell
Get-Service BladeControl.Runtime -ErrorAction SilentlyContinue
Test-Path 'C:\Program Files\BladeControl'
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BladeControl -ErrorAction SilentlyContinue
Test-Path "$env:LOCALAPPDATA\BladeControl"
```

Install with logging throughout, so a failure is diagnosable:

```powershell
msiexec /i BladeControl-0.1.4-win-x64.msi /l*v install.log
```

---

## 1. Clean install

- [ ] Exactly **one** UAC prompt for the whole install.
- [ ] Pre-release terms page appears and states no licence is chosen and the build is unsigned.
- [ ] Final page reports PawnIO state — "detected" or "not detected" with the explanation.
      On a bare VM expect **not detected**.
- [ ] Installs to `C:\Program Files\BladeControl\`, with `Runtime\` beneath it.
- [ ] `BladeControl.UI.exe` at the root; `Runtime\BladeControl.Service.exe` present.
- [ ] `THIRD-PARTY-NOTICES.md` installed alongside.
- [ ] **No `.pdb` files** anywhere under the install directory.
- [ ] Start Menu shortcut **BladeControl** exists and launches the compact panel.
- [ ] Appears in Settings → Apps → Installed apps with publisher *BladeControl Project*,
      version *0.1.4*, and a working help link.
- [ ] `install.log` contains no `return value 3`.

```powershell
Get-Service BladeControl.Runtime | Format-List Name,DisplayName,Status,StartType
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\BladeControl.Runtime' |
    Select-Object ImagePath,Start,DelayedAutostart,ObjectName
sc.exe qfailure BladeControl.Runtime
```

- [ ] Service name `BladeControl.Runtime`, display name `BladeControl Runtime`.
- [ ] `ObjectName` = `LocalSystem`; `Start` = 2; `DelayedAutostart` = 1.
- [ ] `ImagePath` is **quoted** and points into `Runtime\`.
- [ ] `qfailure` shows restart / restart / none, 20 s delay, 1-day reset.
- [ ] Service is **Running** after install.

## 2. Reboot

- [ ] Reboot completes normally; no crash, no service-failure balloon.
- [ ] No Event Log error from `BladeControl Runtime`.

## 3. Service auto-start

- [ ] `Get-Service BladeControl.Runtime` reports **Running** after reboot without intervention.
- [ ] It started *later* than the boot-critical services — confirming delayed start rather than
      contending with the boot storm.

## 4. Sign-in auto-start

- [ ] BladeControl appears after sign-in without being launched.
- [ ] The **compact** panel opens — not the full application.
- [ ] It is at the bottom-right of the monitor under the cursor, ~400 px wide.
- [ ] **No UAC prompt.** The interface must never elevate.
- [ ] Task Manager → Startup apps lists BladeControl and can disable it.
- [ ] `Escape` hides the panel; the tray icon reopens it.

## 5. GUI ↔ service connection

- [ ] If the panel opens before the service is ready it shows
      **Connecting to BladeControl Runtime…**, muted — not a red failure.
- [ ] It connects on its own within ~90 s, with no user action and no aggressive spinning.
- [ ] Header shows **Online**; CPU and GPU temperatures populate.
- [ ] Diagnostics reports the runtime as reachable and shows the PawnIO provenance report.
- [ ] Run a second copy of `BladeControl.UI.exe`: it must connect too, and **not** start a
      second runtime.

Verify the pipe ACL from a standard (non-administrator) account:

- [ ] A standard user's panel connects and can change settings.
- [ ] From another machine, `Test-Path \\<target>\pipe\BladeControl.Runtime.v1` fails —
      network access denied.

## 6. Dynamic cooling — **reference machine only**

Skip on a VM; there is no controller to drive.

- [ ] Fixed mode: dragging sliders produces **no** audible fan change until Apply.
- [ ] Apply changes fan speed once, audibly.
- [ ] Dynamic Start is refused with a clear reason when PawnIO is absent.
- [ ] With PawnIO present and verified, Dynamic Start enters Manual and tracks the curve.
- [ ] Runtime state reads **Running**; telemetry stays live.

## 7. Safe stop — **reference machine only**

- [ ] Dynamic Stop returns fans to firmware Auto.
- [ ] Runtime state reads **Stopped**.
- [ ] Watchdog, telemetry and scheduler tiles relabel as **last session** observations and mute
      — no permanent scary warning after a successful stop.
- [ ] `Stop-Service BladeControl.Runtime` while Dynamic is running also restores firmware Auto
      (safe shutdown on SCM stop), and the service reaches Stopped without timing out.
- [ ] Closing or exiting the interface while Dynamic is running does **not** stop thermal control.

## 8. Upgrade

Build a second MSI with a bumped `BladeControlVersionPrefix`, then install it over the first.

- [ ] One UAC prompt.
- [ ] Old service stopped and removed; **exactly one** `BladeControl.Runtime` service afterwards.
- [ ] Binaries replaced; no duplicate install directory, no orphaned files.
- [ ] Service running again after upgrade, still delayed-auto, recovery actions intact.
- [ ] `%LocalAppData%\BladeControl\ui-settings.json` **preserved** — window size, launch mode,
      close behaviour and the start-with-Windows preference all survive.
- [ ] Sign-in registration still present and pointing at the new path.
- [ ] Installed apps shows only the new version.

## 9. Uninstall

```powershell
msiexec /x BladeControl-0.1.4-win-x64.msi /l*v uninstall.log
```

- [ ] On the reference machine with Dynamic running, uninstall stops the runtime through its
      safe shutdown path — fans return to firmware Auto, not left at a manual target.
- [ ] Service stopped and deleted: `Get-Service BladeControl.Runtime` reports not found.
- [ ] `C:\Program Files\BladeControl\` removed entirely.
- [ ] Start Menu shortcut removed.
- [ ] `HKCU\...\Run\BladeControl` removed for the uninstalling user.
- [ ] Gone from Installed apps.
- [ ] **`%LocalAppData%\BladeControl\` preserved** — this is intended, documented behaviour.
- [ ] **PawnIO still installed and running** if it was before. BladeControl must never remove it.
- [ ] NVIDIA driver untouched.
- [ ] `uninstall.log` has no `return value 3`.

## 10. Reboot and confirm clean removal

- [ ] Reboot completes normally.
- [ ] No BladeControl service, no start-up entry, no Event Log errors.
- [ ] Nothing attempts to launch at sign-in.

---

## Known limitation to verify, not to fix

Sign-in registration is per-user, and a per-machine MSI can only reach the installing user's
`HKCU`. If a **second** user ran BladeControl, their Run entry survives uninstall and points at
a missing executable. Windows ignores it; the user can remove it from Task Manager.

- [ ] Confirm this is the only leftover, and that it is harmless.

## Recording results

Note the Windows build, whether the machine was a VM or the reference laptop, PawnIO
presence/version, and the MSI's SHA-256. Attach `install.log` and `uninstall.log` for any
failure. A failed step is more useful reported than worked around.
