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

A short-name is unique among its siblings, so a reference path identifies an element **within one
model**. Nothing in the standard coordinates package names **across** independently authored models,
and it could not: the standardised packages are common to all of them by construction, and package
trees are commonly organised by domain rather than by vehicle. So two different vehicles routinely
use the same path for different elements.

The ARXML form is specified by AUTOSAR's
[ARXML Serialization Rules](https://www.autosar.org/fileadmin/standards/R20-11/FO/AUTOSAR_TPS_ARXMLSerializationRules.pdf)
(`AUTOSAR_TPS_ARXMLSerializationRules`, R20-11); AUTOSAR itself is summarised on
[Wikipedia](https://en.wikipedia.org/wiki/AUTOSAR).

Instance scope does not cover this. It stops one integration **instance** reconciling away another's,
which is a different collision from two vehicles arriving under **one** instance, and a fleet in one
graph is exactly that.

**So an element's identity is the vehicle plus the path.** Implemented: the job names the vehicle, in
a required setting with no default, and the claim value carries it. The accept pattern refuses the
vehicle-less shape outright, so a provider that forgets the vehicle fails to compose a key rather
than silently merging cars.

## 4. Why a vehicle needs more than one job, and why that is now safe

| bound | value | fixed? |
|---|---|---|
| apiApp proxy job transport | 768 MiB | fixed const, no setting |
| effective files budget after the envelope allowance | 767 MiB | fixed |
| `Integrations:MaxJobFileBytes` | 760 MiB | configurable, and already the whole budget less framing |
| `Integrations:MaxFileBytes` | 128 MiB | configurable |

A multi-bus vehicle's extracts exceed these, and the original problem was not that it needed several
jobs. It was sharper than that:

> **Under one identity, two bus families could not be in the graph at the same time.** A job carrying
> both was refused; a job carrying one withdrew the other, because a complete snapshot withdraws
> whatever it does not mention. So the shared system signals of section 1 were unobservable.

**Resolved by the job declaring a SCOPE** (N2, done). Reconciliation compares against that scope alone,
so a vehicle's CAN extracts and its Ethernet extracts coexist under one identity. Per-*network* scoping
would not have worked: a vehicle's Ethernet extracts commonly declare a single cluster, so two jobs
scoped to that network would still withdraw each other. The scope had to be the job's to declare.

**And the scope is a SEPARATE dimension from the vehicle**, deliberately. A vehicle's CAN and Ethernet
extracts share system signals, and those shared signals ARE the cross-bus join. Folding completeness
into identity would split every one of them into two elements and destroy the thing this line of work
exists to make visible. What makes that work is that a scope lives in the claim property's KEY, so one
element carries a claim per scope and is deleted only when the last of them goes.

The base64 arm is gone, so the table has one fewer row and 200 MiB more headroom: while a job could
arrive encoded, the ceiling had to hold for that transport, and its third came off every job's budget.

## 5. Engine costs, and what was done about them

- **Index removal was O(entire bucket) per element** while an add was logarithmic. An integration's
  claim index puts every element an identity claims under a single key, so that bucket is the whole
  graph. **Fixed:** a removal records that an element is gone, in log time, and the posting list is
  compacted once per halving. Verified by a benchmark whose data it generates itself.
- **The create path issued one HTTP request per index entry.** **Fixed:** `PUT /index/{indexId}/batch`
  takes a list and reports refusals by position.
- **Whole-file decode.** A file's bytes are held for the run and one whole file was decoded to a
  UTF-16 string to be parsed, so peak memory tracked the largest file twice over. **Fixed:** a file can
  be opened as a stream, and the AUTOSAR provider reads its extracts that way. The reader never wanted
  a string - it has always driven an `XmlReader` and materialises only the subtrees it collects - so
  nothing about what it collects changed, pinned by comparing the two paths on the order-sensitive
  serialisation. Measured on a synthetic 15.7 MiB extract: 63.1 MiB less allocated (225.5 → 162.4 MiB),
  four times the document rather than the two the string alone accounts for, and 23% faster as a side
  effect. It also reads MORE documents correctly: an `XmlReader` over the bytes honours the document's
  own encoding declaration, while decoding first could only look for a byte-order mark and otherwise
  assume UTF-8.

## 6. Decisions in force

| # | Decision |
|---|---|
| N1 | A faithful AUTOSAR mirror, roughly 20 entity kinds |
| N2 | A vehicle is part of the claim key, named by the job. **Done**, together with per-scope completeness: a job declares what it is complete OVER, an element may carry several scopes of one identity, and it is deleted only when the last claim goes |
| N3 | Resumable chunked upload into a run-scoped staging area. **Deferred**, with a named trigger - see below |
| N4 | Model the Ethernet-only structure in full |
| N5 | Normalise the Ethernet socket layer onto one set of kinds, keeping the source spelling as a property |
| N6 | Fix the withdrawal cost and the per-entry index writes first. **Done** |

**N3 is deferred rather than open.** Two things changed under it. Per-scope completeness (N2) removed
the correctness need: a source too large for one job now arrives as several jobs that each declare a
scope, and each reconciles against its own scope alone, so nothing withdraws anything else. And the
staging area would have to hold a caller's file BYTES on disk, which contradicts a published guarantee
rather than merely costing work: the runtime mounts no directory, and the one optional mount it does
have (the in-flight run spool) never holds a credential or a file's bytes and is empty whenever nothing
is running. Paying that for a convenience is the wrong trade while the alternative is one extra job.

**The trigger to revisit it:** a single SCOPE whose files exceed what the transport carries in one
request (760 MiB, itself bounded by the proxy's fixed 768 MiB). Splitting cannot help there, because a
scope is the unit reconciliation compares against. If that shows up, N3 is the answer and the disk
guarantee has to be re-decided in the open rather than quietly.

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
Phase 2  vehicle-scoped identity, per-scope completeness         DONE
Phase 3  streaming parse                                         DONE
         staged resumable upload                                 deferred (N3, with a trigger)
Phase 4  the three-bus faithful model                            DONE
Phase 5  the Ethernet detail layer                               DONE
```

Phase 4 landed as four steps, recorded in [plan.md](plan.md): the channel became an element, the
Ethernet cluster reads, the socket layer is read onto three kinds whichever revision spelled it, and
a value produced on CAN traverses to its consumers on FlexRay and Ethernet. Two departures from N1
and N5 as written, both deliberate: 13 kinds rather than "roughly 20", because that count treated the
socket layer's per-revision spellings as distinct kinds and normalising them away is the point; and
no revision DETECTION, because nothing branches on the revision - the spellings do not overlap, so
both are read unconditionally.

The transport step that section 8 called for landed with phase 3: a file's bytes arrive as a multipart
part and nowhere else, and the job ceiling rose to what the transport actually carries (760 MiB, from
560). Base64 was what held it down - a job could arrive encoded, so the ceiling had to hold for that
transport, and its third was subtracted from every job's budget including the ones not using it.

Phase 1's third planned item, a cancellation safe point inside reconcile, was **cancelled by
measurement**. The code says there deliberately is none *because reconciliation is fast*, and that
premise was false only because of the index bug. With the bug fixed the premise holds with orders of
magnitude to spare, so adding an abort would introduce exactly the half-done state that reasoning
warns against.

## 8. Next steps

Every phase is done. What remains is review, and the two things worth reviewing hardest are named
here rather than left to be found:

1. **The Ethernet element NAMES are read from the standard, not from a corpus of exports.** The socket
   layer, the service instances and the coupling ports are all name-driven tables, and no export was
   available to check them against. That is why the socket layer reports what it saw when it finds
   nothing (section 6, N5) - the mitigation is that a wrong name is a table entry and a legible
   report, not silent data loss. The same report does NOT exist for services or coupling ports,
   because absence there is ordinary: an extract with no service layer is not a suspicious extract.
2. **The channel change alters existing graphs.** A re-run of an existing identity reconciles onto the
   new shape, which is what a complete snapshot is for, and `channelCount` is gone. Stated in the docs
   rather than migrated.

## 9. Scope of what is known

The reader is built against the classic platform, schema r4.0, over several AUTOSAR revisions. Several
revisions is variety in the standard, not variety in exporters, so an assumption about how a
particular tool writes an extract should be isolated and named as one rather than treated as general.
The conformance suite judges behaviour, not data variety, and cannot catch a vendor writing something
differently.
