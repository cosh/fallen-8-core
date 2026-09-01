# A vehicle communication model across CAN, FlexRay and Ethernet

Design conclusions for reading AUTOSAR system extracts of a whole vehicle, whatever mixture of buses
it carries. Stated against the standard, so each one can be checked without reference to any
particular export.

---

## 1. Does Ethernet connect to the other buses

**Yes, and the graph already has the edge for it.**

The classic platform provides `GATEWAY` elements with frame, PDU and signal mappings for bus-to-bus
routing, but an extract is not obliged to carry them, and a system-extract view of a vehicle
generally does not. The connection is expressed structurally instead:

- A `SYSTEM-SIGNAL` is the **bus-independent** signal.
- An `I-SIGNAL` is its **per-bus** realisation.
- So one piece of information carried on two buses appears as **one shared `SYSTEM-SIGNAL` with two
  distinct `I-SIGNAL`s**, and never as a shared `I-SIGNAL`.

**Nothing has to be built for the connection.** `CollectSignal` already emits `Implements` from an
I-signal to its `SYSTEM-SIGNAL-REF`, and `implements` is already a published relation type. So the
moment two buses are in one graph, every shared system signal is a junction and
`ecu -sends-> signal -implements-> system-signal <-implements- signal -deliversTo-> ecu` is
traversable across protocols. The connection is a consequence of importing both, not a feature.

The corollary matters more than the finding: **if two buses cannot be in the graph at the same time,
the connection is unobservable.** Section 4 is why they currently cannot.

## 2. What Ethernet is, as a model

**There is no frame layer.** An Ethernet channel carries `PDU-TRIGGERING` directly, and the socket
layer does what a frame does elsewhere, so any traversal that reaches signals through frames finds
nothing on Ethernet.

**`PDU-TRIGGERING` exists on all three protocols.** This is the key structural point for a unified
model: there IS a protocol-neutral object meaning "this PDU appears on this channel". A frame is not
a missing layer on Ethernet; it is how CAN and FlexRay **address** that object, exactly as a socket
connection plus a header id is how Ethernet addresses it.

**`ETHERNET-CLUSTER` sits in the same variant-wrapper triple as `CAN-CLUSTER`** (`-VARIANTS`,
`-CONDITIONAL`, the element), so the existing descendant walk finds it with no new wrapper handling:
structural wrappers carry no short name and so contribute nothing to a reference path.

**Constructs with no counterpart in the current model:** physical channels carrying a VLAN each,
network endpoints, socket addresses with application endpoints over UDP or TCP, static socket
connections, PDU identifiers with header ids, a SOME/IP service layer, switch coupling as
first-class elements, and DoIP.

## 3. Reference paths are not identities

This is the load-bearing conclusion, and it follows from the standard.

AUTOSAR makes a reference path unique **within one system description**. It says nothing about
uniqueness **across** system descriptions, and could not: the standardised platform packages are
present in essentially every extract by construction, and package trees are commonly organised by
domain rather than by vehicle. So two different vehicles routinely use the same path for different
elements.

Instance scope does not cover this. It stops one integration **instance** reconciling away another's,
which is a different collision from two vehicles arriving under **one** instance, and a fleet in one
graph is exactly that.

**So an element's identity is the vehicle plus the path.** Implemented: the job names the vehicle, in
a required setting with no default, and the claim value carries it. The accept pattern refuses the
vehicle-less shape outright, so a provider that forgets the vehicle fails to compose a key rather
than silently merging cars.

## 4. Why a vehicle does not import in one job

| bound | value | fixed? |
|---|---|---|
| apiApp proxy job transport | 768 MiB | fixed const, no setting |
| effective files budget after the envelope allowance | 767 MiB | fixed |
| largest job the JSON arm can deliver, decoded | 575.25 MiB | fixed by 4/3 base64 expansion |
| `Integrations:MaxJobFileBytes` | 560 MiB | configurable |
| `Integrations:MaxFileBytes` | 128 MiB | configurable |

A multi-bus vehicle's extracts exceed these and have to arrive together, because a complete snapshot
withdraws whatever a later job leaves out. The sharp form of the problem is not "it needs several
jobs":

> **Under one identity with today's transport, two bus families cannot be in the graph at the same
> time.** A job carrying both is refused; a job carrying one withdraws the other. So the shared
> system signals of section 1 are unobservable.

**Per-network scoping does not fix it**, because a vehicle's Ethernet extracts commonly declare a
single cluster: two jobs scoped to that network still withdraw each other. The scope has to be
**declared by the job**.

**And the scope must be a SEPARATE dimension from the vehicle.** A vehicle's CAN extracts and its
Ethernet extracts share system signals, and those shared signals ARE the cross-bus join. Folding the
completeness scope into identity would split every one of them into two elements and destroy the
thing this line of work exists to make visible.

## 5. Engine costs, and what was done about them

- **Index removal was O(entire bucket) per element** while an add was logarithmic. An integration's
  claim index puts every element an identity claims under a single key, so that bucket is the whole
  graph. **Fixed:** a removal records that an element is gone, in log time, and the posting list is
  compacted once per halving. Verified by a benchmark whose data it generates itself.
- **The create path issued one HTTP request per index entry.** **Fixed:** `PUT /index/{indexId}/batch`
  takes a list and reports refusals by position.
- **Whole-file decode.** A file's bytes are held for the run and one whole file is decoded to a
  UTF-16 string, so peak memory tracks the largest file. Not fixed; it is the streaming read the
  ARXML spec already names as a prerequisite.

## 6. Decisions in force

| # | Decision |
|---|---|
| N1 | A faithful AUTOSAR mirror, roughly 20 entity kinds |
| N2 | A vehicle is part of the claim key, named by the job. **Done** |
| N3 | Resumable chunked upload into a run-scoped staging area |
| N4 | Model the Ethernet-only structure in full |
| N5 | Normalise the Ethernet socket layer onto one set of kinds, keeping the source spelling as a property |
| N6 | Fix the withdrawal cost and the per-entry index writes first. **Done** |

N5 exists because the socket layer's element names **differ between AUTOSAR revisions with no
overlap**: the older revisions name a socket connection, its bundle and its PDU identifier one way,
and the newer revision names the equivalent constructs differently. A faithful mirror of both would
make the graph's shape a function of the export's revision, so that one layer is normalised with the
source spelling kept as a property. Two further points from the same comparison: CAN and FlexRay are
revision-stable, and **the XML namespace does not identify the revision**, so a reader must read
`xsi:schemaLocation` and fall back to detecting the revision from which vocabulary is present.

## 7. Phases, and where they stand

```
Phase 1  engine: cheap withdrawal, batched claim indexing        DONE
Phase 2  vehicle-scoped identity                                 identity DONE,
                                                                 per-scope completeness OPEN
Phase 3  staged resumable upload, streaming parse                open
Phase 4  the three-bus faithful model                            open
Phase 5  the Ethernet detail layer                               open
```

Phase 1's third planned item, a cancellation safe point inside reconcile, was **cancelled by
measurement**. The code says there deliberately is none *because reconciliation is fast*, and that
premise was false only because of the index bug. With the bug fixed the premise holds with orders of
magnitude to spare, so adding an abort would introduce exactly the half-done state that reasoning
warns against.

## 8. Next steps

1. **Per-scope completeness**, the other half of Phase 2: a job-declared scope on the claim property,
   reconciled per (identity, scope), with an element able to carry several scopes of one identity so
   a shared signal survives losing one and is deleted only on losing the last.
2. **Drop the JSON transport arm** and raise the per-job ceiling toward the proxy budget, which
   removes a code path and roughly halves how many jobs a source needs.

## 9. Scope of what is known

The reader is built against the classic platform, schema r4.0, over several AUTOSAR revisions. Several
revisions is variety in the standard, not variety in exporters, so an assumption about how a
particular tool writes an extract should be isolated and named as one rather than treated as general.
The conformance suite judges behaviour, not data variety, and cannot catch a vendor writing something
differently.
