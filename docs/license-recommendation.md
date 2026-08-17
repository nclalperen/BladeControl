# BladeControl licence recommendation

**Status: recommendation only. No licence has been applied.** Choosing a licence is an
irreversible-in-practice project decision and belongs to the copyright holder, not to the
release engineering that produced this report.

This document records what the [third-party audit](../THIRD-PARTY-NOTICES.md) constrains,
what it does not, and which licence best fits BladeControl specifically.

## What the dependencies allow

| Redistributed dependency | Licence | Constraint it imposes on BladeControl's licence |
|---|---|---|
| LibreHardwareMonitorLib, BlackSharp.Core, DiskInfoToolkit, RAMSPDToolkit-NDD | MPL-2.0 | None. MPL-2.0 §3.3 explicitly permits combining covered files into a "Larger Work" under any licence, provided the covered files' source stays available and notices are preserved. |
| HidSharp | Apache-2.0 | **Rules out GPL-2.0-only.** Apache-2.0's patent-termination and indemnity terms are additional restrictions the FSF treats as incompatible with GPL-2.0. Apache-2.0 *is* compatible with GPL-3.0. |
| .NET 8 runtime, Microsoft.Extensions.* | MIT | None. |
| PawnIO | GPL-2.0-or-later | **None, because it is not redistributed.** This is the single most consequential finding: not bundling PawnIO is what keeps BladeControl's licence choice open. |

So the feasible set is: MIT, BSD, Apache-2.0, MPL-2.0, LGPL, GPL-3.0, AGPL-3.0 — but **not**
GPL-2.0-only, because of HidSharp.

Note the ordering dependency here. If PawnIO were ever bundled, GPL-2.0 obligations would
attach to BladeControl's distribution, and the combination of GPL-2.0 (PawnIO) with
Apache-2.0 (HidSharp) in one distribution is precisely the conflict above. Bundling PawnIO
and keeping HidSharp would force GPL-3.0-or-later, if it were resolvable at all. That is an
argument for keeping PawnIO external quite apart from the ones in the audit.

## Recommendation: Apache-2.0

Ranked against the alternatives for *this* project:

**Why Apache-2.0 fits BladeControl**

- **Explicit patent grant.** BladeControl implements a reverse-engineered vendor HID
  protocol on hardware covered by a manufacturer's patents. Apache-2.0's §3 patent grant
  from contributors, and its patent-retaliation termination, are the clearest protection
  available to contributors of that kind of work. MIT is silent on patents.
- **An attribution mechanism that matches the audit.** Apache-2.0's `NOTICE` file is the
  standard place for exactly the content already produced in THIRD-PARTY-NOTICES.md, and it
  propagates to downstream redistributors by licence term rather than by good manners.
- **A disclaimer proportionate to the risk.** This software drives cooling hardware. §7 and
  §8 disclaim warranty and limit liability in stronger and more specific terms than MIT's
  single sentence — appropriate when a defect can mean a thermal event rather than a wrong
  answer.
- **Compatible with everything shipped**, and one-way compatible with GPL-3.0, so a
  downstream GPL project can still use BladeControl.
- **No copyleft obligation on the project itself**, which keeps the door open for the
  hardware protocol work to be reused — including, potentially, by the vendor.

**Why not the others**

- *MIT* — the obvious default, and a perfectly defensible choice. Rejected only because it
  grants no patent rights and offers a weaker liability disclaimer, both of which matter
  more than usual for reverse-engineered hardware control.
- *MPL-2.0* — would match the largest dependency, but file-level copyleft on a codebase this
  small buys little and complicates redistribution for no clear gain.
- *GPL-3.0* — would guarantee that improvements to the protocol work stay open, which is a
  genuine argument. Rejected as the default because it prevents the vendor or a driver
  project from adopting the protocol implementation, and because copyleft on a utility this
  narrow mostly deters use.
- *GPL-2.0-only* — **not available**; incompatible with HidSharp (Apache-2.0).
- *AGPL-3.0* — irrelevant. There is no network service to close a loophole around.

## What applying it involves

Nothing in this repository presumes the outcome. If Apache-2.0 is accepted:

1. Add `LICENSE` containing the unmodified Apache-2.0 text.
2. Add `NOTICE` with the BladeControl copyright line and a pointer to
   THIRD-PARTY-NOTICES.md.
3. Set `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>` in
   `Directory.Build.props` so every assembly carries it.
4. Replace `installer/License.rtf` — it currently states pre-release terms and explicitly
   says no licence has been chosen — with the chosen licence.
5. Update the README licence section and §5 of THIRD-PARTY-NOTICES.md.
6. Record the decision in `CHANGELOG.md`.

Step 4 matters: the installer today shows an honest "no licence selected yet, evaluation
only" agreement. Shipping a public release with that text would be a mistake, and shipping
one with a licence the copyright holder never agreed to would be a worse one.

## Open question that is not a licensing question

Reverse-engineered vendor protocol implementations carry a residual risk that is unrelated
to which OSS licence is chosen: the vendor's own view of the protocol documentation and any
applicable anti-circumvention law. This report does not address that, and no licence choice
mitigates it. If public release is intended, it is worth an explicit decision rather than an
implicit one.
