# Max-fan protocol research (blocked)

Status: research note only. BladeControl does not construct, whitelist, send, or
expose either command described here.

## Known evidence

- The archived, target-specific `tdakhran/razer-ctl` implementation sends SET
  command `0x070F` with a one-byte payload and requires Custom performance mode:
  <https://github.com/tdakhran/razer-ctl/blob/main/librazer/src/command.rs#L845-L858>.
- Its typed values define `0x02` as enable and `0x00` as disable:
  <https://github.com/tdakhran/razer-ctl/blob/main/librazer/src/types.rs#L550-L558>.
- Its Blade 16 2023 Synapse capture annotations contain the same two SET
  mappings (`070F 02` enable, `070F 00` disable):
  <https://github.com/tdakhran/razer-ctl/blob/main/data/README.md#L195-L245>.
- The implementation attempts GET command `0x078F` with a one-byte zero
  argument and reads response argument zero:
  <https://github.com/tdakhran/razer-ctl/blob/main/librazer/src/command.rs#L861-L864>.

The same capture table records `0D01` fan-RPM frames with a leading `0x01`,
while the target implementation constructs them with leading `0x00`. Fan
Control V1 follows the explicitly selected and CRC-tested `00 <zone>
<rpm-div-100>` framing. This discrepancy is another reason not to infer nearby
command framing without focused captures.

## Unresolved before any future implementation

- No `0x078F` GET capture in the cited table establishes the exact request and
  response framing on the reference firmware.
- The expected `remaining_packets` behavior for `0x078F` is not established.
- Exact response data size, echo/selector behavior, CRC vector, firmware status,
  and transaction matching still need dedicated capture and hardware proof.
- Interaction with Custom mode, CPU/GPU levels, and exit/recovery behavior needs
  a bounded acceptance design before a SET can be considered safe.

Until those questions are resolved, `0x070F` and `0x078F` remain absent from
packet factories, the production whitelist, public APIs, and CLI commands.
