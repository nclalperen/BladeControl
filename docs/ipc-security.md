# Runtime IPC security model

The named pipe `\\.\pipe\BladeControl.Runtime.v1` is the only way to reach BladeControl's
hardware control. Anything that can write to it can change fan speeds and performance levels.
It is therefore a privilege boundary, and this document records how it is defended and why
the obvious cheaper options were rejected.

Policy implementation: [`RuntimePipeSecurity`](../src/BladeControl.Ipc/RuntimePipeSecurity.cs).

## What changed, and why it had to

During hardware validation the runtime ran as a console process owned by the interactive user,
and the pipe used `PipeOptions.CurrentUserOnly` on both ends. That was adequate then: server
and client were the same account, and Windows enforced the match.

Shipping the runtime as an SCM service running as **LocalSystem** breaks that on both sides:

- On the **server**, `CurrentUserOnly` would restrict the pipe to LocalSystem, so the user
  interface could never connect.
- On the **client**, `CurrentUserOnly` asserts that the server runs as the connecting user —
  which is now deliberately false.

The tempting fixes are both wrong. Removing `CurrentUserOnly` and accepting the default DACL,
or granting `Everyone`, would publish a hardware-control channel to every process on the
machine, including sandboxed and network-authenticated ones.

## Threat model

| # | Threat | Defence |
|---|---|---|
| 1 | **Remote caller over SMB.** Windows exposes named pipes on the network; a pipe is remotely reachable unless refused. | Access is granted to `INTERACTIVE` (S-1-5-4), which appears only in tokens from a local logon — network logons are excluded *by construction*, not by blocklist. `NETWORK` (S-1-5-2) is additionally denied. The server also verifies locality per connection with `GetNamedPipeClientComputerName`. |
| 2 | **Anonymous or service-account caller.** A sandboxed or unprivileged daemon attempting hardware control. | `ANONYMOUS LOGON` (S-1-5-7) denied. No grant to `Everyone`, `Users`, `Guests`, `NetworkService` or `LocalService`. A process without an interactive token has no path in. |
| 3 | **Pipe squatting / server spoofing.** Any process may create a pipe by name. A malicious one could publish the runtime's pipe name first, then feed the UI fabricated telemetry or harvest what it sends. | `FILE_CREATE_PIPE_INSTANCE` is granted only to LocalSystem and Administrators, so an unprivileged process cannot add an instance to the real pipe. Independently, the client checks the connected pipe's **owner SID** and refuses to talk to a pipe not published by a privileged account. |
| 4 | **ACL tampering.** A logged-on standard user widening the pipe's permissions. | `WRITE_DAC` and `WRITE_OWNER` are never granted to interactive users. |
| 5 | **Resource exhaustion by a hostile peer.** | The 64 KiB message ceiling is enforced on both ends, before deserialisation, and the pipe buffers are sized to it. Unchanged from the validated protocol. |

## The ACL

| Identity | Rights | Rationale |
|---|---|---|
| `NETWORK` (S-1-5-2) | **Deny all** | Defence in depth behind the INTERACTIVE grant and the per-connection locality check. |
| `ANONYMOUS LOGON` (S-1-5-7) | **Deny all** | No unauthenticated hardware control, ever. |
| `LocalSystem` | Full control | The service account; owns the pipe and creates its instances. |
| `BUILTIN\Administrators` | Full control | Diagnostics and management. |
| `INTERACTIVE` (S-1-5-4) | Read, Write, Synchronize | The UI and CLI. Explicitly **not** `CreateNewInstance`, `WriteDac` or `WriteOwner`. |
| everyone else | (no rule) | MSI-style default deny: absence of a grant is refusal. |

The descriptor is applied **at pipe creation** via `NamedPipeServerStreamAcl.Create`, not
afterwards. A pipe created with a default DACL and then secured is briefly reachable by the
wrong callers, and that window is exactly when an attacker who is watching for the pipe name
would connect.

## Why interactive users get write access at all

Because they are the physical operators of the laptop, and fan control is what the product is
for. The pipe ACL answers "who may ask"; it is deliberately **not** the thermal-safety
boundary. Every state-changing request is still validated by the runtime against its own
gates — authoritative telemetry present, provenance verified, runtime state correct, single
in-flight command, no automatic retry — regardless of who asked.

The consequence worth stating plainly: on a machine where an untrusted person has an
interactive session, that person can change fan and performance settings. That is the same
privilege they already have over the physical machine, including its power button.

## Client-side server verification

`RuntimePipeSecurity.VerifyServerIsPrivileged` reads the connected pipe's security descriptor
and requires the owner to be one of exactly two identities:

| Trusted owner | Hosting mode it corresponds to |
|---|---|
| `LocalSystem` | The installed service. The MSI registers it with `Account="LocalSystem"`, and nothing less can open Razer HID or read CPU MSRs. |
| `BUILTIN\Administrators` | The documented elevated console host used for development. |

**Why Administrators and not the developer's user account.** Windows takes an object's default
owner from the creating token's `TOKEN_OWNER`, which is a distinct field from `TOKEN_USER`. For
an elevated administrator token `TOKEN_OWNER` is the Administrators group (S-1-5-32-544), so
that — not the user SID — is the owner an elevated console host actually produces. A
*non-elevated* host would produce an ordinary user SID, and that is deliberately **not**
trusted: accepting it would mean trusting any pipe published by any logged-on user, which is
exactly the squatting attack this check exists to stop. A non-elevated host cannot reach the
hardware anyway.

**Why LocalService and NetworkService are not trusted.** The runtime can never run as either —
they lack the privileges to reach the hardware — so accepting them would only widen the trusted
set to include the kind of low-privilege sandboxed service account a hostile process is most
likely to be running under.

A pipe squatted by a standard-user process has that user's SID as owner and is refused.

This reads the owner rather than inspecting the server process, deliberately: obtaining a
handle to a LocalSystem process is something a standard user cannot do, so a process-identity
check would fail for every legitimate client. Reading the descriptor needs only
`READ_CONTROL`, which the ACL grants.

## Preserved from the validated implementation

- The typed JSON protocol, its operations and DTOs — unchanged.
- Protocol version and request-ID validation — unchanged.
- The 64 KiB bound on both directions — unchanged.
- One server instance, one request per connection — unchanged, so the UI's single-flight
  gate still funnels every request through one conversation.
- Per-connection locality check via `GetNamedPipeClientComputerName` — unchanged.

## Tests

`tests/BladeControl.Service.Tests/PipeSecurityPolicyTests.cs` asserts the allow/deny decision
for each well-known identity, that interactive clients cannot create pipe instances or rewrite
the descriptor, that a created pipe's owner is the creating token's `TOKEN_OWNER` (a
regression test for an earlier incorrect `TOKEN_USER` assumption), that the client's verdict
matches the production trusted-owner policy exactly, and that every identity outside the two
trusted ones fails verification. These are policy tests against the built descriptor; they neither open
hardware
nor start a service.
