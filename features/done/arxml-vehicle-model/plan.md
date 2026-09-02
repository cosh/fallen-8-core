# Plan: the vehicle communication model

Phases 1 to 3 are done. What follows is phases 4 and 5 of
[findings.md](findings.md) section 7, broken into steps that each leave the suite green and the
graph usable.

**Where this stands:** every step is DONE. Step 4 was brought forward ahead of step 3, because it is
the deliverable and does not depend on the socket layer.

## Step 1 - the channel becomes first-class (DONE)

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

## Step 2 - the Ethernet cluster reads (DONE)

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

## Step 3 - the socket layer, normalised (DONE)

10. `endpoint`, `socket`, `connection`, with `partOf`, `boundTo`, `serverPort`, `clientPort`, and
    `carries` from a connection to a PDU.
11. One vocabulary table per revision spelling, read onto those three kinds, with `sourceSpelling`
    kept on the element.
12. `xsi:schemaLocation` is read from the document element, because the XML namespace does not
    identify the revision. **The vocabulary fallback was dropped**, and the reason is worth keeping:
    nothing branches on the revision. The spellings do not overlap, so reading for both is
    unambiguous and a detector would be a second thing to get wrong for no gain. What the file
    declared still matters for the report below, and the report already lists the vocabulary it saw,
    which is the same information and more actionable.
13. The wrong-guess report of spec 4.5: an Ethernet channel whose socket layer yielded nothing
    reports what it did see.

Tests: each spelling reads onto the three kinds; the two produce one SHAPE, guarded against passing
vacuously (two empty shapes are equal, which is what a reader recognising neither would produce); a
port reference naming the application endpoint rather than the socket address still lands on the
socket, while one that is neither is reported rather than resolved onto its parent; a channel with an
unrecognised socket layer reports what it saw rather than importing an empty bus, once per run; and a
CAN bus is never asked about a socket layer, so it never reports one missing. Mutation-checked by
removing the newer spelling from the table: the two tests that must fail do.

## Step 4 - the three-bus traversal (the deliverable) (DONE)

14. A fixture of three extracts - CAN, FlexRay, Ethernet - sharing two system signals, imported as
    one source.
15. A test that walks `ecu -sends-> signal -implements-> system-signal <-implements- signal
    -deliversTo-> ecu` where the two ECUs are on different protocols, and asserts the protocols
    differ.
16. The docs' AUTOSAR section gains the kinds, the relations and that traversal as its example.

## Step 5 - the detail layer (phase 5) (DONE)

17. `service`: SOME/IP provided and consumed instances, one kind with the role as a property, `partOf`
    the socket that offers or consumes them. NOT joined to each other on `serviceId`: an instance is
    per socket and the file states no relationship between them, so matching them is a query rather
    than an inference. The identifier is a property precisely so that query can be written.
18. `coupling`: switch coupling ports, `partOf` their ECU, with `connectedTo` between them read from
    the channel because a link belongs to neither end. One edge per link, in the direction the file
    states it.

Both were additive and neither changed a shape step 4 depends on, which is why they were last.

Tests: both roles read with their identifiers; a service belongs to its SOCKET rather than to the
application endpoint, which is not an element; two instances of one service stay two elements; the
coupling topology is one edge per link; a half-stated link is neither an edge nor a diagnostic while a
link to an undeclared port is reported; and a CAN extract grows neither kind. Mutation-checked by
removing the consumed-service entry and the coupling-connection element name: four tests fail.

## Not doing, and why

- **N3, the staged resumable upload.** Deferred with a named trigger; see findings.md section 6.
- **Gateways.** Spec section 5.
- **A per-revision faithful socket layer.** That is the outcome N5 exists to prevent.
