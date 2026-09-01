# CAN clusters for the AUTOSAR reader

**Status: IMPLEMENTED.**

## What it does

The reader reads `CAN-CLUSTER` alongside `FLEXRAY-CLUSTER`, through a protocol table rather than a
second cluster walk. A vehicle whose buses are a mixture imports as ONE graph: an ECU on two buses
is one element attached to both, and a frame in one extract can carry a signal defined in another.

## Contract

- Cluster reading is table-driven over the protocol-conditional fields: the cluster, channel, frame
  and frame-triggering element names, the protocol value, and the triggering payload. A further
  protocol is a row, and a protocol with **no frame layer** has to be expressible as one.
- CAN carries a `canId` and an addressing mode where FlexRay carries a slot, a base cycle and a
  cycle repetition. Both are properties on the same kind, protocol-conditional and documented as
  such, so a query for "what does this frame carry" never enumerates protocols.
- Baudrate, protocol name and protocol version are read for every protocol, because the standard
  carries them on every cluster conditional.
- A re-declared CONTAINER path unions its children; a re-declared LEAF path keeps first-wins.
- A shared cluster path is reported by its own diagnostic, separate from the ordinary
  shared-catalogue one, because merging is right for one bus split across extracts and lossy if the
  two declarations are really different buses. Nothing in the file distinguishes those, so the merge
  is reported rather than silent.
- A set carrying a bus this version does not read imports what it can and names what it skipped. A
  set carrying nothing it reads fails the run rather than reporting an empty network, because an
  empty complete snapshot would delete the network a previous run described. One extract of a set
  having no cluster is normal, since the gate is over the union.

## Impact on existing features

The engine is untouched. The REST contract is untouched except for the per-job ceiling. The provider
descriptor's wording, the descriptor snapshot, the docs page and the Integrations screenshot all
move together. Ethernet remains unread, and is a different model rather than another protocol.
