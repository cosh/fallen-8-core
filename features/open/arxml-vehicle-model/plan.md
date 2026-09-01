# Plan: the vehicle communication model

Phases 1 to 3 are done. What follows is phases 4 and 5 of
[findings.md](findings.md) section 7, broken into steps that each leave the suite green and the
graph usable.

## Step 1 - the channel becomes first-class

The structural change, and the one that touches existing behaviour.

1. `ArxmlKinds.Channel`, `ArxmlRelations.PartOf`.
2. `CollectCluster` emits one `channel` element per distinct channel short name, `partOf` its
   network, carrying `name` and the network's `protocol`.
3. `attachedTo` is emitted to the channel as well as to the network, from the same
   `COMMUNICATION-CONNECTOR-REF` the channel already carries.
4. `channelCount` is removed from `network`.
5. The descriptor's `EntityKinds`/`RelationTypes` grow; regenerate the descriptor snapshot and
   recapture `screen-integrations.png`.

Tests: a channel per channel and not per cluster variant (the counting bug the removed
`channelCount` documented); FlexRay A and B are two channels of ONE network; a CAN cluster has one;
`attachedTo` reaches both the network and the channel; and the existing CAN/FlexRay assertions that
change do so deliberately.

## Step 2 - the Ethernet cluster reads

6. A third `BusProtocol` entry with **no frame layer**: `ETHERNET-CLUSTER`,
   `ETHERNET-PHYSICAL-CHANNEL`, and nothing for frame or frame triggering. The table's own
   documentation already anticipates this entry; the walk must tolerate the absent names rather than
   look for an element called "".
7. `ETHERNET-CLUSTER` leaves the unread-cluster list, and the `unreadCluster` diagnostic stops firing
   for it.
8. VLAN: `vlanId` and `vlanName` on an Ethernet channel.
9. `PDU-TRIGGERING` under an Ethernet channel is already walked by the shared code; confirm the PDU
   and signal layers resolve with no frame in between, which is the point of the protocol-neutral
   triggering.

Tests: an Ethernet-only extract produces a network, its channels, and PDUs and signals reachable
without a frame; a query for frames on an Ethernet network finds none rather than failing; the
unread-cluster diagnostic no longer names Ethernet.

## Step 3 - the socket layer, normalised

10. `endpoint`, `socket`, `connection`, with `partOf`, `boundTo`, `serverPort`, `clientPort`, and
    `carries` from a connection to a PDU.
11. One vocabulary table per revision spelling, read onto those three kinds, with `sourceSpelling`
    kept on the element.
12. Revision detection from `xsi:schemaLocation`, falling back to which vocabulary is present.
13. The wrong-guess report of spec 4.5: an Ethernet channel whose socket layer yielded nothing
    reports what it did see.

Tests: both spellings produce one shape; both detection paths; a channel with an unrecognised socket
layer reports rather than importing an empty bus; a socket bound to an endpoint on another channel is
reported rather than silently joined.

## Step 4 - the three-bus traversal (the deliverable)

14. A fixture of three extracts - CAN, FlexRay, Ethernet - sharing two system signals, imported as
    one source.
15. A test that walks `ecu -sends-> signal -implements-> system-signal <-implements- signal
    -deliversTo-> ecu` where the two ECUs are on different protocols, and asserts the protocols
    differ.
16. The docs' AUTOSAR section gains the kinds, the relations and that traversal as its example.

## Step 5 - the detail layer (phase 5)

17. `service`: SOME/IP provided and consumed service instances, and what they reference.
18. `coupling`: switch coupling ports, so the topology below an Ethernet cluster is visible.

Both are additive and neither changes a shape step 4 depends on, which is why they are last.

## Not doing, and why

- **N3, the staged resumable upload.** Deferred with a named trigger; see findings.md section 6.
- **Gateways.** Spec section 5.
- **A per-revision faithful socket layer.** That is the outcome N5 exists to prevent.
