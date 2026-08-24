# Code signing

## Current state

**Pre-release builds are unsigned.** Windows shows an unknown-publisher warning on the MSI and
SmartScreen may block it. This is stated plainly rather than worked around: BladeControl does
not fake, borrow, or self-sign a signature that would imply trust it has not earned.

The integrity check available today is the SHA-256 hash published as `SHA256SUMS.txt` with every
release. Verify before installing:

```powershell
Get-FileHash -Algorithm SHA256 .\BladeControl-0.1.3-win-x64.msi
```

## What is already in place

The build is structured so signing can be added without restructuring anything:

- **`build/pack.ps1` is the single packaging entry point**, used identically by a developer and
  by the release workflow. Signing hooks go in one place and apply to both.
- **Publisher and version metadata are already authoritative.** `Directory.Build.props` sets
  `Company`, `Product` and `Copyright` on every assembly; the MSI carries `Manufacturer`,
  `ProductName`, `ProductVersion` and ARP links. A signature adds cryptographic proof of an
  identity the packages already assert.
- **Deterministic Release builds**, so a rebuild of the same commit produces the same binaries
  and the published hashes remain meaningful.
- **No secret material anywhere in the repository.** There is no certificate, no thumbprint, no
  key path, and no signing step to accidentally run with the wrong credentials.

## Where signing belongs

Two steps, in this order, both inside `pack.ps1`:

1. **After publish, before the MSI is built** — sign the two executables and the BladeControl
   assemblies in `artifacts/publish/{ui,service}`. Signing after the MSI is authored would leave
   the packaged files unsigned, and the MSI's file hashes would not match.
2. **After the MSI is built** — sign the MSI itself, then compute the hashes. Hashing must be
   last, or the published hash describes a file nobody downloads.

Sketch of the insertion points:

```powershell
# after the publish + audit steps
if ($env:BLADECONTROL_SIGN -eq 'true') {
    & signtool sign /fd SHA256 /tr $env:TIMESTAMP_URL /td SHA256 `
        /csp $env:SIGNING_CSP /kc $env:SIGNING_KEY_CONTAINER `
        (Get-ChildItem -Recurse $publishRoot -Include 'BladeControl*.exe','BladeControl*.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}

# after the MSI is built, before Get-FileHash
```

Timestamping (`/tr`) is not optional: without it every signature expires with the certificate,
and already-shipped installers start warning.

## Certificate requirements

| | |
|---|---|
| Type | OV or EV code-signing certificate |
| Storage | Hardware token, HSM, or a cloud signing service. Since June 2023 the CA/Browser Forum baseline requires private keys on FIPS 140-2 Level 2 hardware — a PFX file on disk is no longer issuable for public trust. |
| Reputation | A fresh OV certificate still accrues SmartScreen reputation over downloads and time. EV certificates get immediate reputation. For a project distributing a hardware-control MSI, EV is worth the cost difference. |

## Credential handling — non-negotiable

- **Never in git.** Not the certificate, not a thumbprint that identifies it, not a token PIN,
  not a key-vault URL with credentials.
- **In GitHub Actions**, use repository or environment secrets, or better, a cloud signing
  service with OIDC federation so no long-lived secret exists at all.
- **Gate on a protected environment.** Release signing should require an environment with
  required reviewers, so a compromised workflow file cannot sign on its own.
- **Signing runs only on tag builds.** Pull-request workflows must never have access to signing
  credentials — a fork's pull request must not be able to reach them.
- **Log nothing.** No `/v`-style verbose signtool output into public build logs, no echoing of
  environment variables.

The release workflow as committed has no signing step and no secret references, so there is
nothing to leak until a credential is deliberately added.

## Driver signing

Not applicable, and worth stating: BladeControl ships no kernel-mode code. PawnIO — the only
kernel driver in the picture — is authored, signed and distributed by its own project and is
[deliberately not bundled](../THIRD-PARTY-NOTICES.md). Nothing BladeControl ships requires
Microsoft attestation or WHQL.

## Until then

The README says the build is unsigned, the installer's pre-release terms say it, and the release
notes should say it too. A user who is told "this is unsigned, here is the hash" is better served
than one who meets an unexplained SmartScreen warning.
