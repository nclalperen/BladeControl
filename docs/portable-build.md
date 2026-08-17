# BladeControl portable build

**The MSI installer is the normal way to install BladeControl.** This archive exists for
developers and advanced users who want the binaries without an installer. If you are not
sure which you want, use the installer.

## What the portable archive does not do

The portable build is a plain file copy. It does **not**:

- install the `BladeControl.Runtime` Windows service;
- start the runtime automatically with Windows;
- register BladeControl to start when you sign in;
- appear in Installed Apps / Add or Remove Programs;
- create Start Menu shortcuts;
- configure service failure recovery.

Without the service registered and running, the user interface has nothing to connect to. It
will start, show **Connecting to BladeControl Runtime…**, then settle into an offline state
with every hardware control disabled. That is correct behaviour, not a fault: the UI is a
thin IPC client and never touches hardware itself.

## Layout

```
BladeControl/
    BladeControl.UI.exe          user interface (no elevation required)
    Runtime/
        BladeControl.Service.exe runtime host — owns all hardware access
    THIRD-PARTY-NOTICES.md
    README-PORTABLE.md           this file
```

Both trees are self-contained x64 builds; no .NET runtime installation is required.

## Running the runtime in the foreground

For development, the runtime host can run as a console process instead of a service. It
requires an **elevated** prompt, because it opens Razer HID and CPU MSR interfaces:

```
Runtime\BladeControl.Service.exe console
```

Add `--verbose` for extra diagnostics. `Ctrl+C` stops it through the same safe shutdown path
the service uses: firmware fan mode is restored, the event stream is drained, and any active
thermal session is stopped exactly once.

The executable deliberately **cannot** install, remove, start or stop a Windows service.
That is the installer's job, and keeping it out of the runtime host means a compromised or
mistaken invocation cannot reconfigure the machine.

Only one runtime host may own the hardware at a time. A machine-wide singleton is taken
before any device is opened, so if the installed service is running, a console host exits
immediately with an explanatory message rather than fighting it for the controller. Stop the
service first:

```
sc stop BladeControl.Runtime
```

## Where settings live

`%LocalAppData%\BladeControl\ui-settings.json` — the same location the installed build uses.
A portable copy therefore shares preferences with an installed copy, and nothing is written
next to the executables.

## PawnIO

BladeControl does not bundle PawnIO. Without it, static performance and fan control work
normally; closed-loop thermal control is refused because the authoritative CPU package
temperature it depends on is unavailable. See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)
for the full reasoning and the provenance requirements.
