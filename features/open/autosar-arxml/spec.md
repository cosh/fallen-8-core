# AUTOSAR ARXML integration (autosar-arxml)

Status: open, IMPLEMENTED on `feature/autosar-arxml` and awaiting the review gate. Phases 0 to 4 of
[plan.md](plan.md) landed: the vocabulary entry, the reader, the provider registered as the fourth
shipped blueprint, the docs and the recaptured screenshot. Move this record to `features/done/` when
the branch merges.

Two contract points below were CHANGED by what the implementation and its adversarial review found,
and the changed statement is the one that holds:

- The summary template carries **no literal word next to a hole** (section 9). Hole collapse removes
  the punctuation around a hole an element cannot fill but it cannot remove a word, so the earlier
  `unit {arxml.unit}` would have ended every ECU, frame and PDU summary with a dangling "unit".
- The reader reports a **third diagnostic**, `arxmlUndecidablePortDirection` (section 7), because a
  port whose direction is neither IN nor OUT cannot decide which way its edge points, and defaulting
  silently turned a receiver into a sender.

## 1. What this is, and what it is for

A fourth shipped integration provider for the `fallen-8-integrations` runtime: it reads an
AUTOSAR classic-platform **system extract** (an `.arxml` file, the standard XML interchange
format of the AUTOSAR development partnership) and describes the **communication matrix** it
carries as a snapshot: the network, its ECUs, frames, PDUs, signals, system signals and
scaling methods, with the send/receive flow between them.

ARXML is the format in which the automotive industry exchanges E/E network descriptions:
every OEM ships communication matrices to its suppliers this way, so one provider covers
files from any manufacturer. The value of putting one in a graph is impact analysis
("which ECUs hear this signal, in which frame, in which slot"), topology review (who talks,
who listens), semantic discovery (the word "kilometer" finds the odometer signal, section
9), and release-to-release diffing, which the integration contract supplies for free: a
system extract is by construction a complete description of its network, so a complete
snapshot of a new release withdraws exactly what the release removed.

This provider follows the integrations contract exactly and adds nothing to it except one
identifier-vocabulary entry and one case-preserving canonicaliser. Everything the contract
already owns (identity, resolution, reconciliation, deletion safety, credential handling,
conformance) is not restated here; [features/done/integrations/spec.md](../../done/integrations/spec.md)
owns those rules.

## 2. Scope

**In (v1):** classic-platform system extracts in the AUTOSAR r4.0 XML namespace
(`http://autosar.org/schema/r4.0`, which covers every 4.x release), containing at least one
**FlexRay** cluster. One file per integration instance, read from the runtime's files
directory like the CSV provider's file.

**Out (each with a named revisit trigger, section 13):** CAN and Ethernet clusters, the
AUTOSAR 3.x namespace, the software-component level, byte-layout on relations, and DBC/LDF.
Semantic search is **in** scope (section 9); it rides the existing summary-embedding
surface and adds no machinery.

## 3. The provider descriptor

| Field | Value |
| --- | --- |
| `id` | `autosar-arxml` |
| `displayName` | AUTOSAR system extract (ARXML) |
| `description` | Reads an AUTOSAR classic-platform system extract (ARXML, schema r4.0) from the runtime's files directory and describes the communication matrix it carries: the network, its ECUs, frames, PDUs, signals, system signals and scaling methods, with the send/receive flow between them. |
| `settings` | one: `file` (kind `Text`, required). Help mirrors the CSV provider: the NAME of a file such as `network.arxml`, never a path; the runtime opens it. |
| `entityKinds` | `network`, `ecu`, `frame`, `pdu`, `signal`, `system-signal`, `compu-method` |
| `claimTypes` | `arxml-path` |
| `relationTypes` | `attachedTo`, `sends`, `deliversTo`, `contains`, `carries`, `secures`, `implements`, `scaledBy` |
| `canObserveCompleteState` | `true` (this is the point: a system extract describes its whole network) |
| `readOnly` | `true` |
| `entitySummaryTemplate` | `{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, unit {arxml.unit}` (the semantic payload; why each hole is there is section 9) |

There is deliberately **no cluster or channel filter setting**: a setting must never narrow
what a complete snapshot covers (the standing integrations rule). A file with several
clusters is described whole.

## 4. The graph model

All properties carry the `arxml.` prefix. An absent value is absent, never an empty string.

| Kind | One per | Properties |
| --- | --- | --- |
| `network` | `FLEXRAY-CLUSTER` | `arxml.name`, `arxml.protocol` (`flexray`), `arxml.channelCount`. The network is the CLUSTER and never a channel: FlexRay channels A and B are physical redundancy of one bus carrying one schedule, so an element per channel would split one network in two and double every frame |
| `ecu` | `ECU-INSTANCE` | `arxml.name` |
| `frame` | `FLEXRAY-FRAME` | `arxml.name`, `arxml.frameLengthBytes`, `arxml.slotId`, `arxml.baseCycle`, `arxml.cycleRepetition` (timing from the frame's triggering) |
| `pdu` | each PDU element (`I-SIGNAL-I-PDU`, `NM-PDU`, `N-PDU`, `DCM-I-PDU`, `SECURED-I-PDU`, `CONTAINER-I-PDU`, `GENERAL-PURPOSE-PDU`, `GENERAL-PURPOSE-I-PDU`, `USER-DEFINED-I-PDU`, `USER-DEFINED-PDU`, `MULTIPLEXED-I-PDU`) | `arxml.name`, `arxml.pduKind` (the element name), `arxml.lengthBytes`, `arxml.descDe`, `arxml.descEn` |
| `signal` | `I-SIGNAL` | `arxml.name`, `arxml.lengthBits`, `arxml.initValue`, `arxml.baseType`, `arxml.descDe`, `arxml.descEn`, `arxml.unit` (denormalised at parse time through `implements` then `scaledBy`; absent when the chain is incomplete) |
| `system-signal` | `SYSTEM-SIGNAL` | `arxml.name`, `arxml.descDe`, `arxml.descEn` |
| `compu-method` | `COMPU-METHOD` | `arxml.name`, `arxml.category`, `arxml.unit`. The unit is the referenced `UNIT`'s **display name** (`km`), falling back to its short name when it has none: `UNIT_KM` is an identifier and would defeat the semantic query of section 9, which is the reason the unit is read at all |

`descDe`/`descEn` are the `DESC/L-2` language variants; only `DE` and `EN` are read, others
are dropped without a diagnostic (they are prose variants, not data).

Relations, each addressed by the target's `arxml-path` claim:

| Relation | From | To | Derived from |
| --- | --- | --- | --- |
| `attachedTo` | ecu | network | the channel's `COMMUNICATION-CONNECTOR-REF`s (the connector path names the ECU) |
| `sends` | ecu | frame or signal | a `FRAME-PORT` / `I-SIGNAL-PORT` with `COMMUNICATION-DIRECTION` `OUT`, referenced from the frame or signal triggering |
| `deliversTo` | frame or signal | ecu | the same ports with direction `IN` |
| `contains` | frame | pdu | `PDU-TO-FRAME-MAPPING` |
| `contains` | pdu | signal | `I-SIGNAL-TO-I-PDU-MAPPING` |
| `carries` | container pdu | pdu | `CONTAINED-PDU-TRIGGERING-REF`, resolved through the channel's `PDU-TRIGGERING` map |
| `secures` | secured pdu | payload pdu | `PAYLOAD-REF`, resolved the same way |
| `implements` | signal | system-signal | `SYSTEM-SIGNAL-REF` |
| `scaledBy` | system-signal | compu-method | `COMPU-METHOD-REF` in `PHYSICAL-PROPS` |

`sends`/`deliversTo` point WITH the data flow, so a directed path query from a sending ECU
to a receiving ECU traverses `sends` then `deliversTo` without reversing an edge. Repeated
(entity, type, target) relations are emitted once; repetition is normal in the source (one
signal mapped at several byte positions) and carries no diagnostic.

## 5. Identity: the `arxml-path` claim

Every entity asserts exactly one claim: its **AUTOSAR reference path**, the slash-separated
short-name chain from the package root to the element (`/ISignals/.../SignalName`). The
standard makes this path the file's own reference mechanism, so it is unique within a file
by construction and every cross-reference in the file resolves against it by exact string
match.

New vocabulary entry in `identifier-vocabulary.v1.json`:

```json
{
  "type": "arxml-path",
  "strength": "strong",
  "scope": "instance",
  "canonical": "trim",
  "accept": "^(/[A-Za-z][A-Za-z0-9_]*)+$",
  "description": "An AUTOSAR reference path: slash-separated short-names from the package root to one element. Case is preserved because short-names are case-sensitive identifiers."
}
```

- **Scope `instance`**, because equal paths in two different extracts do not assert an
  overlap: every extract contains `/AUTOSAR_Platform/BaseTypes/uint8`, and two instances
  describing two networks in one namespace must not share elements. Same instance, new
  release file: paths resolve, and that is the release diff.
- **`trim` is a new canonicaliser** (case-preserving `Trim()`), added to the closed set.
  The existing `trimUpper`/`trimLower` fold case, and folding an AR path could unify two
  elements the standard allows to differ only by case, which is exactly the
  wrong-element-attribution failure the vocabulary exists to prevent. The accept pattern is
  anchored, per the vocabulary loader's standing rule.
- **Identity is positional, faithfully.** A signal that moves to another PDU package in the
  next release gets a new path, so the old element is withdrawn and a new one created. That
  is what the standard itself says happened (the reference identity changed); the provider
  does not invent continuity the source does not assert. `arxml.name` stays queryable as a
  property for name-based lookups.

## 6. Parsing rules

One streaming pass with `XmlReader` over the text `ProviderContext.ReadFileAsync("file")`
returns; the provider never opens a file itself.

- **Hardened:** `DtdProcessing.Prohibit`, no `XmlResolver`. A DTD in the input fails the
  run (XXE and entity-expansion are not risks worth tolerating in a file a browser upload
  can reach via the files directory).
- **Namespace gate:** the root must be `AUTOSAR` in the r4.0 namespace; anything else is a
  `ProviderSourceException` naming what was found.
- **Path reconstruction:** a stack of (element, short-name) frames; an element's AR path is
  the joined short-names of its named ancestors plus its own. Structural list elements have
  no `SHORT-NAME` and contribute nothing, which is exactly the standard's path semantics.
  This was validated by resolving every reference in a production extract (section 8).
- **Interest set:** the elements of section 4 are loaded as subtrees and read; everything
  else (`ADMIN-DATA`, data mappings, timing extensions, the ~99% of the file that is not
  communication topology) streams past without allocation.
- **Two-stage resolution:** references are collected during the pass and resolved
  afterwards against the path table. `carries`/`secures` resolve through the channel's
  `PDU-TRIGGERING` map (the file points at triggerings, not PDUs). Frame timing (`SLOT-ID`,
  `BASE-CYCLE`, `CYCLE-REPETITION`) comes from the frame's `FLEXRAY-FRAME-TRIGGERING`; port
  direction comes from the connector's port declarations.

## 7. Failure versus empty, and diagnostics

"I could not look" must never become "there is nothing there". The rules:

- Unreadable file, non-XML content, a DTD, a foreign root namespace, or a file with **no
  FlexRay cluster**: `ProviderSourceException`. The run fails and withdraws nothing. A
  comm-matrix provider handed a file that is not a comm matrix has not observed an empty
  network; it has failed to observe.
- A missing `file` setting: `ProviderConfigurationException` (via `Required`).
- A file that parses and has a cluster always yields a `complete` snapshot.

Named diagnostics (ride the snapshot into the job report, entity-level, never fatal):

| Code | When | Subject |
| --- | --- | --- |
| `arxmlUnresolvedReference` | a collected reference names a path the file does not define; the relation is dropped. Also raised when a compu method's unit is undefined, where the unit's short name stands in for its display name | the referenced path |
| `arxmlDuplicatePath` | two elements compose the same AR path; the second is skipped, and nothing it referenced is recorded either | the path |
| `arxmlUndecidablePortDirection` | a port a triggering names declares a direction that is neither IN nor OUT, so the flow edge is dropped rather than pointed by a guess | the port path |

## 8. Scale, honestly

Validated offline (2026-08) against a production FlexRay system extract of a current
vehicle platform: 82 MB, 1,050,264 XML elements, 490 distinct element names. A prototype of
exactly this parsing design processed it in 1.4 s into 12,261 entities and 28,155 relations
with **zero unresolved references**, and the resulting graph loaded and answered impact,
degree and path queries correctly. The file and every value derived from it stay outside
the repository (section 11).

- The file arrives as **one string** (`ReadFileAsync`); with the reader over it, transient
  memory is roughly 3x file size. Acceptable for a job runner at the validated size; a
  streaming overload on `ProviderContext` is deferred with a trigger (section 13).
- The snapshot (~12k entities, ~28k relations) is well within what the applier already
  handles: writes go through the batched `PUT /vertices` / `PUT /edges` wire path and claim
  lookups read in batches. A release import is a batch job measured in minutes at worst; no
  new machinery is needed and none is added.

## 9. Semantic search over the matrix

**The requirement, as an acceptance example:** with embeddings enabled, a semantic query
for "kilometer" must surface the odometer signal, even though signal names are cryptic
codes and nothing forces the word to appear in any of its text. This is a first-class
requirement of the feature, not a nice-to-have: comm-matrix signal names are unguessable,
and finding the right one is the single most common question asked of a matrix.

**The mechanism is entirely existing machinery.** The integrations runtime already embeds
one summary per entity when BOTH halves of the opt-in are set (the descriptor declares a
template; the job asks and names the embedding), re-embedding only entities whose data
changed. A `VectorIndex` bound to that embedding name projects itself from the imported
vectors, and `POST /embedding/search` embeds a query text once and runs constrained kNN
against it. The provider's entire contribution is putting the right text into the summary.

**Why the template has exactly these holes** (`{kind} {arxml.name}, {arxml.descEn},
{arxml.descDe}, {arxml.unit}`, with no literal word beside any hole):

- `arxml.name`: the identifier engineers already know, so name fragments also hit.
- `arxml.descEn` **and** `arxml.descDe`: the source prose is bilingual and queries arrive
  in either language. A multilingual embedding model puts "kilometer" next to
  "Kilometerstand"; the compose default (bge-m3) is multilingual. Honest constraint: under
  a non-multilingual model the German half degrades, and the docs recipe says so.
- `arxml.unit`: the reason the unit is denormalised onto the signal (section 4). An
  odometer signal whose descriptions say "total distance" never mentions kilometers; its
  unit `km` does. Holes collapse for kinds that lack the property, per the template rules.

**The operator recipe** (the docs page owns the worked version):

1. Run the job with the embedding opt-in set and an embedding name (say `arxml-summary`);
   the apiApp's embedding provider must be on (`GET /status`).
2. Create a `VectorIndex` bound to `embeddingName: arxml-summary`.
3. `POST /embedding/search` with `{"indexId": ..., "text": "kilometer", "k": 10,
   "label": "signal"}`.
4. Feed the hit ids into the traversal surface (`/path`, `/subgraph`, property reads):
   similarity search lands ON the graph, so "who receives the kilometer signal" is the
   kNN hit followed by one `deliversTo` hop.

**Acceptance, falsifiably:**

- Offline (the suite): the rendered summary of the fixture's odometer signal contains its
  name, both descriptions and the unit; removing any hole from the template turns the test
  red. No embedding provider is needed for this half.
- Live (the merge gate, plan phase 4): the synthetic fixture network is run into a compose
  environment with the embedding sidecar, and the recipe's "kilometer" query must rank the
  odometer signal above the fixture's near-miss (a speed signal, unit `km/h`, whose
  existence keeps this a ranking statement rather than a substring match). Recorded in the
  PR; not a CI gate, because it needs a live model.

## 10. What a re-run means (the release diff)

Same instance, new release file: the snapshot declares `complete`, so reconciliation
withdraws every claim the instance asserted that the new file no longer contains, deletes
elements on their last claim under the standing deletion-safety gates, creates what is new,
and leaves the unchanged resolved in place. The change feed then IS the release diff. This
needs zero provider code beyond declaring completeness; it is the reason the provider
exists as an integration rather than as a converter script.

## 11. Conformance, fixtures, confidentiality

- The provider implements `IObservableProvider` and passes the conformance suite offline
  (`TheShippedArxmlProviderConforms`, alongside the three existing blueprint tests).
- **Every fixture is hand-authored synthetic ARXML**: a small invented network (two ECUs,
  two frames, a secured and a container PDU, a handful of signals) exercising every rule in
  sections 4 to 7, including the odometer signal and its near-miss speed signal that
  section 9's acceptance needs, plus one negative fixture per diagnostic and per failure
  rule.
- **Hard rule:** no fixture, test, doc, comment or commit message may contain content
  derived from any real OEM export: no OEM names, no real signal/ECU/network names, no real
  descriptions. The merge gate greps for this (plan, phase 4).

## 12. Impact on existing features

| Asset | Impact |
| --- | --- |
| `identifier-vocabulary.v1.json` + `Canonicalisers` | one new entry, one new canonicaliser (`trim`); the per-entry canonicaliser/accept tests grow accordingly, and any test pinning the entry count updates |
| `features/done/integrations/provider-descriptors.json` | regenerate (`scripts/update-provider-descriptor-snapshot.ps1`); `ProviderDescriptorSnapshotTest` pins the fourth descriptor |
| `docs/images/screen-integrations.png` | **must be recaptured**: the capture replays the descriptor snapshot and the provider list gains a row |
| `docs/src/content/docs/integrations.md` | gains the provider's section including the worked semantic-search recipe of section 9 (one home; no new page) |
| Stale counts | every "three shipped providers/descriptors" phrasing goes count-free or becomes four: known sites are the root `CLAUDE.md` quality-gates bullet and the docs page; phase 3 sweeps for the rest |
| OpenAPI snapshot, MCP coverage, architecture diagrams, NL-assist dataset | untouched: no new REST operation, no new deployable, no new channel |
| F8 Studio | zero code change; the integrations screen renders any descriptor (pinned by the existing descriptor-fixture test) |
| Engine, apiApp | untouched |
| `features/done/integrations/spec.md` | historical record; not rewritten. The living list of shipped providers is the descriptor snapshot and the docs page |

## 13. Deliberately not built

| Not built | Trigger to reopen |
| --- | --- |
| CAN and Ethernet clusters | a real CAN or Ethernet system extract shows up. The entity model already fits; only the triggering and timing fields differ |
| AUTOSAR 3.x namespace | a real 3.x file shows up |
| The software-component level (data mappings, component ports) | software-architecture queries are asked of the graph, not network ones |
| Byte positions on `contains` relations | byte-layout queries. Blocked on the snapshot contract, which has no relation properties; that contract change is its own decision |
| `sourceVersion` from tool-specific `ADMIN-DATA` | release-diff UX wants a human release name. The standard does not carry one; only tool-specific blocks do |
| A streaming file read on `ProviderContext` | an extract well past ~200 MB. Today the file arrives as one string and the validated 82 MB is comfortable |
| DBC / LDF readers | someone brings the format. Different parsers, not extensions of this one |
| A provider-side embedding call, or embeddings beyond the one summary per entity | never: the summary-template opt-in (section 9) is the semantic surface, and the provider only supplies text. Per-kind templates, or a second embedding per entity, wait for a kind that demonstrably needs a different payload |
| A cluster or channel filter setting | never: a setting must not narrow what a complete snapshot covers |
