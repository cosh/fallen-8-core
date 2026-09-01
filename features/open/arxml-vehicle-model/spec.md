# A vehicle communication model across CAN, FlexRay and Ethernet

Status: **open**. Phases 1 to 3 are done (see [findings.md](findings.md) section 7); this spec covers
phases 4 and 5, the model itself.

## 1. What this is for

A vehicle is not a bus. It is several buses of different protocols whose signals cross between them,
and the crossing is the interesting part: a wheel-speed value produced on CAN and consumed by a
driver-assistance function on Ethernet is ONE piece of information with a realisation on each bus.
The reader already imports CAN and FlexRay. What it does not do is read Ethernet at all, and an
Ethernet cluster is not "one more entry in the protocol table" because its structure below the
cluster is different in kind.

The goal is a model in which a query can walk from an ECU on one bus to an ECU on another, through
the shared signal that joins them, without knowing which protocols are involved.

## 2. What is already true, and must stay true

From [findings.md](findings.md), established against the standard rather than against any export:

- **The cross-bus join already exists in the model.** A `SYSTEM-SIGNAL` is bus-independent; an
  `I-SIGNAL` is its per-bus realisation; the reader already emits `implements` from the latter to the
  former. So two buses in one graph share their system signals, and nothing needs building for the
  join itself. What was missing was the ability to have both buses in the graph at once, which
  phase 2 fixed.
- **The network is the CLUSTER, not the channel.** A FlexRay cluster's channels A and B are physical
  redundancy of one bus carrying one schedule; splitting them would double every frame and describe
  two networks no ECU experiences as separate.
- **An element's identity is the vehicle plus the AUTOSAR path.** A reference path is unique within
  one model and coordinated across models by nothing, so a fleet in one graph needs the vehicle in
  the key.
- **`PDU-TRIGGERING` exists on all three protocols.** It is the protocol-neutral "this PDU appears on
  this channel". A frame is not a layer Ethernet is missing; it is how CAN and FlexRay ADDRESS that
  object, exactly as a socket connection plus a header id is how Ethernet addresses it.

## 3. The problem this spec has to solve

**A channel is a different thing on Ethernet.** On CAN it is the one channel a cluster has. On
FlexRay it is redundancy. On Ethernet **a channel is a VLAN**: a couple of dozen distinct broadcast
domains under one cluster, and which VLAN an ECU sits on is a fact an engineer asks about directly.
Today the channel is not an element at all - it is walked through to reach triggerings, and reduced
to a `channelCount` property whose own documentation already says the number will mean something
else once Ethernet arrives.

**Everything Ethernet addresses a PDU with is below the channel and has no counterpart in the
model:** network endpoints, socket addresses with application endpoints over UDP or TCP, socket
connections, and PDU identifiers carrying header ids.

**And the socket layer's element names differ between AUTOSAR revisions with no overlap** (N5). A
faithful mirror of every revision would make the graph's SHAPE a function of which revision an
extract was written against, so the same vehicle exported twice would import as two different
graphs.

## 4. The model

### 4.1 Kinds

Kept as they are: `network`, `ecu`, `frame`, `pdu`, `signal`, `system-signal`, `compu-method`.

Added:

| kind | what it is | protocols |
|---|---|---|
| `channel` | one physical channel of a cluster. A VLAN on Ethernet, redundancy on FlexRay, the single channel on CAN | all |
| `endpoint` | a network endpoint: an address on a channel | Ethernet |
| `socket` | a socket address and its application endpoint: a port over UDP or TCP | Ethernet |
| `connection` | a socket connection: the pairing of two sockets that carries PDUs, with their header ids | Ethernet |
| `service` | a SOME/IP service instance, provided or consumed (phase 5) | Ethernet |
| `coupling` | a switch's coupling port, where the topology below the cluster lives (phase 5) | Ethernet |

That is 13, not the "roughly 20" of N1, and the difference is deliberate: N1 counted the socket
layer's revision-specific spellings as distinct kinds, which N5 then normalises away. A kind per
spelling is exactly the outcome N5 exists to prevent.

### 4.2 Relations

Kept: `attachedTo`, `sends`, `deliversTo`, `contains`, `carries`, `secures`, `implements`,
`scaledBy`.

Added:

| relation | from → to | meaning |
|---|---|---|
| `partOf` | `channel` → `network`, `endpoint`/`socket`/`connection` → `channel` | structural containment. ONE relation for it rather than one per pair, because the question "what is under this bus" should not need a type per level |
| `boundTo` | `socket` → `endpoint` | which address a port is on |
| `serverPort` | `connection` → `socket` | the standard's own role names, kept because a connection is not symmetric even over TCP |
| `clientPort` | `connection` → `socket` | |

`carries` is reused for `connection` → `pdu`: a socket connection transporting a PDU is the same
statement a container PDU makes about the PDUs inside it, and inventing a second word for it would
make "what carries this PDU" a two-query question.

`attachedTo` is emitted BOTH to the network and to the channel, which is not a duplicated edge but
two different facts: the network one answers "is this ECU on this bus" protocol-neutrally, and the
channel one answers "which broadcast domain is it in", which only exists on Ethernet. On CAN the two
coincide, and that is a property of CAN rather than a redundancy in the model.

### 4.3 Properties

New, all conditional on what the source carries:

- `channel`: `name`, `protocol` (denormalised from its network so a channel can be filtered alone),
  and on Ethernet `vlanId` and `vlanName`.
- `endpoint`: `name`, `address`, `ipVersion`, `addressSource` (fixed, DHCP, …), `networkMask` or
  `prefixLength` as the source spells it.
- `socket`: `name`, `port`, `transport` (`udp` or `tcp`).
- `connection`: `name`, `transport`, `sourceSpelling` (see 4.4), `headerIds` count.

`channelCount` on `network` is REMOVED. Its own documentation says nothing should be built on it and
that it would mean something else on Ethernet; with channels first-class it is a worse answer to a
question the graph can now answer properly.

### 4.4 Revision handling (N5)

**Detection.** The XML namespace does NOT identify the revision. The reader reads
`xsi:schemaLocation` on the document element, and where that is absent or unrecognised falls back to
which vocabulary the document actually contains. The detected revision is recorded on the snapshot
as a diagnostic-level fact, not as a property of every element: it is a fact about the FILE, and an
element's shape must not depend on it.

**Normalisation.** The socket layer is read onto the three kinds above whichever spelling a revision
uses, and the source's own element name is kept on the element as `sourceSpelling`. So a query is
written once, and an operator can still see what the file said. This is the one place the reader
deliberately does NOT mirror the standard, and the reason is stated on the element itself.

### 4.5 What a wrong guess must do

The socket layer is the part of this work where the reader is most likely to be wrong about an
element name, because the names differ by revision and this reader is built against the standard
rather than against a corpus of exports. A wrong name must not become silent data loss.

So: when an Ethernet channel is read and its socket layer yields NOTHING - no endpoint, no socket, no
connection - the reader reports one diagnostic per channel naming the element names it actually saw
under it, bounded to a handful. That turns "we guessed the name wrong" from an empty graph into a
report an operator can act on and a maintainer can read a fix out of.

This is deliberately not a per-element "unrecognised" diagnostic: an extract contains thousands of
elements this reader has no interest in, and reporting them would bury everything that matters.

## 5. Out of scope

- **`GATEWAY` elements.** The classic platform can express bus-to-bus routing explicitly, and an
  extract is not obliged to carry it. The structural join through `SYSTEM-SIGNAL` is what this model
  relies on; reading gateways as well would add a second, sometimes-present answer to the same
  question.
- **DoIP** and diagnostics over IP: a different subject from the communication matrix.
- **Anything that requires a corpus of real exports to get right.** Assumptions about how a
  particular tool writes an extract are isolated and named as such, never treated as general.

## 6. Impact on existing features

| area | impact |
|---|---|
| provider descriptor | `EntityKinds` and `RelationTypes` grow, so `provider-descriptors.json` must be regenerated and `screen-integrations.png` recaptured (the screen renders the descriptor's "writes" column) |
| engine / REST | none: the reader emits the same snapshot contract |
| MCP | none: no new REST operation |
| existing CAN/FlexRay graphs | the SHAPE changes - `channel` elements appear, `attachedTo` gains channel targets, `channelCount` disappears. A re-run of an existing identity reconciles to the new shape, which is what a complete snapshot is for. Stated in the docs rather than migrated |
| docs site | `integrations.md`'s AUTOSAR section describes the kinds and relations, so it changes with them |
| Studio | nothing hard-codes these kinds; the Samples gallery carries no AUTOSAR sample |
| NL-assist | no dataset entry mentions AUTOSAR kinds. No retrain entry needed |

## 7. How it is judged

- The reader's tests are hand-authored synthetic extracts. **No content derived from a real
  manufacturer's export appears in this repository in any form**, which is a rule of the feature.
- A three-bus fixture - CAN, FlexRay and Ethernet in one set, sharing system signals - and a test
  that walks ECU to ECU across protocols. That traversal is the deliverable; everything else is
  scaffolding for it.
- Parity for what already worked: the existing CAN and FlexRay tests keep passing, except where the
  channel change deliberately alters the shape, and those changes are asserted rather than adjusted
  away.
- The revision handling is judged on BOTH detection paths (`xsi:schemaLocation` present, and absent
  with vocabulary as the fallback) and on the normalisation producing one shape from two spellings.
