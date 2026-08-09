# Integration identity - Specification

> **Status:** Open, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/integration-runtime` (shared with its two
> siblings; branch-only workflow, no GitHub issue/PR).
>
> **Siblings.** [integration-runtime](../integration-runtime/spec.md) hosts the providers and owns
> the write path and the conformance verifier; [integration-blueprints](../integration-blueprints/spec.md)
> is the provider contract and the three reference providers. **This document is the one to review
> hardest**: it is the part most likely to be wrong and the hardest to change once data exists.
>
> **Phasing lives once**, in the shared [plan](../integration-runtime/plan.md), because all three
> specs are one branch with one sequence.
>
> **Hard dependency.** This design is only implementable on top of the P0 items in
> [platform-integrity-audit](../platform-integrity-audit/spec.md), and it is shaped by that
> audit's findings rather than merely blocked on them. Section 3 explains which constraint forced
> which decision. The audit is the single home for those items' evidence and rejections; this
> spec does not restate them.
>
> **Revision history:**
> - *2026-08-09a* - initial design, after the platform audit.

## 1. What this owns

Integrations return entities. Entities do not know vertex ids. Something has to decide that the
device UniFi calls `44:d2:44:aa:bb:cc` and the inverter Fronius calls `UniqueID 476` are, or are
not, the same thing in the graph, and has to keep that decision correct across restarts, crashes,
re-syncs, and the removal of a whole integration.

This document owns that: the claim model, the identifier vocabulary, resolution and merge rules,
the claim set and reconciliation, how the graph represents all of it, and where this model meets
the [semantic layer](../../done/semantic-layer/spec.md), which is a second entity-resolution
system already in the product.

**Integrations do not resolve identity. The runtime does.** A provider returns claims; it never
returns a vertex id, never queries the graph, and never learns whether its entity was created or
matched. That is the single most important boundary in the feature, because it is what lets an
agent write a provider without understanding any of this document.

## 2. The claim model

An entity carries typed identity claims and relations that reference other entities **by claim**,
never by id:

```
entity:
  kind:       device
  identity:   [ { type: mac,    value: 44d244aabbcc, strength: strong },
                { type: serial, value: 12345678,     strength: strong },
                { type: ipv4,   value: 192.168.1.20, strength: weak } ]
  properties: { fronius.model: "Symo 8.2-3-M", fronius.pvPeakW: 8200 }
  relations:  [ { type: hasIp, target: { type: ipv4, value: 192.168.1.20 } } ]
```

**Strength is not the provider's to declare.** A provider states the claim's *type*; the
vocabulary states the strength. A provider that could call its own weak identifier strong would be
able to cause a false merge, which is the worst outcome this design can produce. The snapshot
schema accepts a declared strength only so the conformance verifier can reject a provider whose
declaration disagrees with the vocabulary, which catches an author who has misunderstood.

### 2.1 The identifier vocabulary is a versioned data file

Not prose, and not code. One file, shipped with the runtime, validated on load, and the only
authority on what an identifier type means.

| Field | Meaning |
|---|---|
| *type* | the wire name, for example *mac* |
| *strength* | *strong* or *weak*. Only *strong* can cause a merge |
| *scope* | *global*, *provider*, or *instance*. See below |
| *canonical* | the normalization rule, for example lowercase and strip separators for *mac* |
| *validate* | the accept pattern. A value that fails is a diagnostic, never a silent drop |

**The *scope* field is not decoration, and it is the vocabulary's least obvious requirement.** A
provider-native id is only unique inside whatever namespace the provider guarantees, and providers
differ:

- *unifi-device-id* is a UUID. Globally unique in practice, so scope *provider* is honest and
  scope *global* would also be safe.
- *fronius-unique-id* is a **short integer string** (values like `476` and `3113` appear in real
  captures). It is unique only within one inverter's own Solar API. Two Fronius installations both
  reporting `1` are different devices. So its scope is **instance**, and its canonical claim key
  must include the integration instance id.

Getting this wrong produces a false merge between two users' devices, or between two sites of one
user, on first sync. It is the single highest-consequence entry in the whole feature, which is why
it is data with a validator rather than a sentence in a document.

Starter set, extend as needed:

| type | strength | scope |
|---|---|---|
| *mac*, *serial*, *imei* | strong | global |
| *ipv4*, *ipv6*, *hostname* | weak | global |
| *unifi-device-id*, *unifi-client-id*, *unifi-site-id* | strong | provider |
| *fronius-unique-id* | strong | **instance** |

An unknown type is **rejected**, not ignored. A provider emitting one fails the conformance suite.

### 2.2 The canonical claim key

Resolution is exact-string matching on a canonical key, computed by the runtime, never by a
provider:

```
global scope     <type>:<canonical(value)>                 mac:44d244aabbcc
provider scope   <type>@<providerId>:<canonical(value)>    unifi-device-id@unifi:2f3c...
instance scope   <type>@<instanceId>:<canonical(value)>    fronius-unique-id@garage-inverter:476
```

One canonicalization site, one test per vocabulary entry.

## 3. How the graph represents this, and why it has no choice

Five verified platform constraints forced this representation. They are recorded here because the
representation looks over-engineered until you know them, and because a future reader will
otherwise try to "simplify" it back into a broken shape.

| Constraint (evidence in the audit) | What it forecloses | What it forces |
|---|---|---|
| Element properties are **scalar-only** (18 types, no arrays) over REST, and a structured value does not survive a reload with its type | A set-valued or JSON-blob claim set | One property per claim, and one property per claimant |
| There is **no compare-and-set** anywhere in the REST contract | Read-modify-write on any shared property | Every property must have exactly one writer |
| The claim index is **derived state destroyed by three ordinary operations** and is snapshot-only | The index as system of record | Claims live as element **properties**; the index is a projection that can always be rebuilt |
| `HEAD /trim` **renumbers every element id** in place, unwaited and agent-reachable | Caching or persisting any element id across requests | Every cross-request handle is a claim key |
| The runtime keeps **only an ephemeral cache** (your decision), so the graph is the sole system of record | Any pending state outside the graph | Merge candidates and claim sets are graph state |

### 3.1 Identity claims as reserved properties

Each claim is one property on the element, using a reserved prefix in the style the engine already
established for embeddings (`$embedding:`), which the engine parses by prefix and deliberately
hides from all-property content search:

```
$identity:0  =  "mac:44d244aabbcc"
$identity:1  =  "serial:12345678"
$identity:2  =  "ipv4:192.168.1.20"
```

The ordinal exists because a real device carries several claims of one type: a UniFi access point
has more than one MAC. The **value** is the canonical claim key, so one index over the whole
prefix serves every type.

### 3.2 The claim set as one property per claimant

```
$claim:<integrationInstanceId>  =  "<integrationInstanceId>"
```

The key is the claimant, so **each claimant is the only writer of its own property**. That is what
removes the need for compare-and-set: two integration instances asserting the same device never
touch the same property, so neither can lose the other's claim. Assertion is an idempotent set;
withdrawal is an idempotent property remove, which always succeeds and is therefore replay-safe.

The **value repeats the instance id** so that one index over the `$claim:` prefix, keyed on
values, answers "every element this instance claims" in a single lookup. That is what makes
reconciliation and integration-removal one round-trip instead of a graph scan.

**The value is deliberately stable across syncs.** It carries no last-confirmed timestamp. A
timestamp would make every sync rewrite every claim property, which would break the zero-mutation
invariant outright. Staleness is reported from the instance's own run status, not from the graph.

### 3.3 Edges carry claim sets too, and get a synthetic identity

An edge has no intrinsic identifier, and the platform offers no way to ask "is there already an
edge of this type between these two vertices" in one call. So an edge gets a derived claim from the
canonical keys it connects:

```
$identity:0  =  "edge:<sourceClaimKey>|<type>|<targetClaimKey>"
```

which makes edge resolution one lookup in the same index, and makes edge creation idempotent. The
source and target keys used are each element's **primary** claim key: the strongest claim, and
among equals the lexicographically first, so the derived key is stable and independent of the
order a provider listed its claims in. If a key grows impractical, the fallback is a hash of the
same canonical string; the readable form is preferred while it fits, because it is debuggable.

### 3.4 The claim index is one derived projection

Two prefix-bound indices, both maintained by the writer thread, both backfilled at creation, both
rebuilt from element state on load:

- **the identity index**, over the `$identity:` prefix, keyed on values,
- **the claim-set index**, over the `$claim:` prefix, keyed on values.

This is the platform audit's W4 (generalizing the bound-projection mechanism from an embedding
name to a property prefix). The precedent stretches naturally: the engine already parses reserved
property ids **by prefix** and matches indices on the parsed suffix, and a bound index already
persists only a header and rebuilds from element state.

**One honest note on where the precedent does not reach.** The bound vector index binds to an
exact reserved key and indexes a value it validates. Here the index binds to a prefix and indexes
the value verbatim. That is a real extension, not a pure reuse, and it is the one place this design
asks the engine for something new. The fallback if it proves larger than expected is the rebuild
primitive alone with explicit index writes, which keeps correctness and loses self-maintenance.
The fallback is **not** WAL-logging index writes; that is rejected in the audit with a trigger.

### 3.5 The ephemeral cache, and the subscription that keeps it honest

The runtime caches claim key to element id in memory only. Consequences, all deliberate:

- **Every restart is a cold cache.** The first sync after a restart resolves from the index.
- **The cache is invalidated, never warmed, by the change feed.** The feed carries
  `propertySet(elementType, id, label, key)` with **no value**, by design, so it can say "this
  element's claim properties changed" and cannot say what to. That is sufficient for invalidation
  and insufficient for anything else.
- **An open subscription on `GET /changefeed` is mandatory, not an optimization.** It is the only
  in-band signal for the three operations that destroy the index (`/tabularasa`, a save-game load,
  a dropped index manifest entry). The runtime treats a resync event as *ensure index, then
  rebuild*, and must refuse to compute withdrawals until it has done so.
- **Any element id is valid only within one request.** `HEAD /trim` can renumber between two calls.

## 4. Resolution and merge rules

Given a snapshot entity with claims C:

1. Canonicalize every claim; reject unknown types and failed validations into diagnostics.
2. Look up each **strong** claim key in the identity index.
3. **No strong hit:** create the element, write its claims and this instance's claim property.
4. **Exactly one element matched by all strong hits:** that is the element. Add any strong claim it
   does not carry, add this instance's claim property, write this instance's namespaced properties.
5. **Two or more distinct elements matched by different strong claims:** this is a **strong-strong
   collision** and it is *not* a merge. It means either a duplicate created before both claims were
   known, or a genuine identifier conflict. Raise a merge candidate at highest confidence, write
   nothing that would entangle the two, and record a diagnostic. Silently merging two elements that
   already carry other integrations' claims and properties is destructive and irreversible.
6. **Weak claims never resolve on their own.** A weak-only overlap with an existing element
   produces a **merge candidate**, never a merge, never an entanglement.

**Merge on strong identifiers only. Never on weak identifiers alone. There is no configuration
that changes this.**

### 4.1 Merge candidates

A candidate is graph state, because the runtime has nowhere else durable to put it and because
Studio, agents and any other consumer should all see the same queue with no new read routes:

- a vertex labelled for the purpose, carrying the two claim keys, the evidence (which weak claim
  overlapped, or which strong claims collided), the raising instance, and a status,
- **keyed on claim keys, never on element ids**, so `HEAD /trim` cannot silently retarget a pending
  human decision at a different device,
- idempotent: the same overlap on the next sync finds the existing candidate and does not raise a
  second,
- and it survives a restart and a re-sync because it is in the graph, which is exactly what the
  acceptance test asserts.

**Confirming a candidate is recorded as a durable user-asserted strong claim on the element.** Not
a merge flag, not a resolved-candidate row: a claim, in the same vocabulary, with a
*user-asserted* type whose scope is global and whose strength is strong. That is why the merge
survives the next sync without special-casing: the next resolution simply finds a strong hit.
Rejecting a candidate records a durable negative assertion, so the same overlap is not raised
again forever.

### 4.2 Relations resolve the same way

A relation names its target by claim, so an integration can attach to an entity it has never seen
and did not create. If the target claim resolves, the edge is created or matched. If it does not,
the relation becomes a **dropped-relation diagnostic** with the unresolved claim key, and the next
sync retries it. Nothing is dropped silently, and a provider never needs to know whether the thing
it is pointing at exists yet.

This is the mechanism the Fronius blueprint exercises: it attaches readings to a device that
UniFi created.

### 4.3 Prohibited, permanently

**Embedding similarity, semantic similarity, and any vector distance are never merge signals, at
any strength, under any configuration.** Two identical smart plugs produce identical text and
therefore identical vectors, and they are different devices. The failure is not probabilistic; with
a declarative summary template it is deterministic.

Similarity may **rank candidates for human review** and may do nothing else. This is stated here so
that a future reader who notices the graph has a vector index does not "improve" resolution with
it.

## 5. Reconciliation and deletion

Reconciliation is per integration instance, and it is a set difference:

1. One lookup in the claim-set index for this instance gives **every element it currently claims**.
2. The resolved element set from the current snapshot is what it claims **now**.
3. The difference is the withdrawal set.
4. Withdraw by **removing this instance's claim property** from each element in the withdrawal set,
   in one batch transaction.
5. For each withdrawn element, read its remaining claim set. **An element is deleted only when its
   last claim is withdrawn.** Anything still claimed is left exactly as it is, including the
   properties this instance wrote, until a policy says otherwise.

**Deletion has a durability precondition.** The one mutation that re-syncing cannot undo is
deleting an element another claimant still asserts, and the way that happens is by reading a claim
set that lost entries. So the runtime must not delete when the platform reports a degraded WAL or a
truncated replay (audit W5). It withdraws, records a diagnostic, and defers the deletion. Deferring
a deletion is recoverable; performing a wrong one is not.

**Removing an integration** offers to withdraw all of its claims through exactly the same path,
deleting only what nothing else asserts. There is no second code path for it.

## 6. Where this meets the semantic layer

The semantic layer already extracts named entities from documents and folds them into an entity
graph. It is a second entity-resolution system in the same product, and the collision is sharper
than "two systems that must not overlap".

**The disagreement, stated plainly.** The semantic layer deduplicates *Entity* vertices by an exact
dictionary key over `(normalized, type)`, where *normalized* is a casefolded name string. That is
already an automatic strong-equality merge **on a name**. Under this document's vocabulary a name
is a weak identifier. So the two models do not merely need a boundary; they disagree about what a
name is.

**The resolution, and it is not a papering-over.**

1. **The semantic layer's internal dedup is correct for what it does and is not changed.** Merging
   two mentions of one name into one *Entity* is a different question from cross-provider device
   identity. Nothing in this feature touches it.
2. **The two claim spaces are disjoint.** An *Entity* vertex never enters the identity index and
   never carries a `$claim:` or `$identity:` property. Only integration-asserted elements do. NLP
   and mention-derived entities therefore cannot participate in resolution at all, which is a
   stronger guarantee than "they carry weak claims at most".
3. **Where they overlap, the primitive is a relation, not a merge.** A weak-signal overlap between
   an *Entity* and an integration-asserted device raises a candidate whose confirmation creates a
   *refersTo* edge and **does not merge the vertices**. A mention of "Symo 8.2-3-M" in a PDF is
   *about* the inverter; it is not the inverter. Merging them would destroy the distinction between
   a thing and the text about it, which is the entire value of the semantic layer. This is a better
   outcome than the merge the original design contemplated, not a weaker one.

**The one change proposed on the semantic-layer side, and it is about duplication rather than
identity.** That feature works around the non-durable dictionary index with a hand-rolled
startup sweep that rebuilds the entity index by scanning every *Entity* vertex; its own comment
states the hazard it prevents ("an Entity vertex can outlive its index key; without this, the next
ingest would create a duplicate"). This feature needs the identical mechanism. Writing a second
copy is exactly the duplication this repository rejects, so the audit's W4 rebuild primitive should
subsume both, and the semantic layer's sweep should be deleted in favour of it. That is proposed
rather than assumed, because it touches a shipped feature's boot path.

## 7. The two-integration cases, as acceptance tests

Both are required, both run offline against fixtures and a fake target.

**Strong overlap.** A observes a device by MAC. B observes the same MAC plus a serial and adds
readings. Assert: **one** element; **two** claim properties; both property namespaces present; the
serial added as a new claim on the existing element; and removing A leaves B's data and the element
intact.

**Weak-only overlap.** A observes a device by MAC and IP. B observes only a serial and the same IP.
Assert: **two** elements; **one** merge candidate; **no** automatic merge; confirming the candidate
merges them; and the merge **survives a restart and a re-sync**, because the confirmation was
recorded as a user-asserted strong claim rather than as candidate bookkeeping.

**Plus the ones the platform audit made necessary:**

- an unchanged re-sync issues **zero write calls**, asserted on the call channel,
- a cold cache after restart produces the same resolution as a warm one,
- a resync event (tabula rasa, save-game load) triggers ensure-index-then-rebuild, and the
  following sync creates **no** duplicates,
- a degraded-durability signal blocks deletion and records a diagnostic,
- a simulated `HEAD /trim` between two requests does not retarget a pending candidate or a claim
  write,
- a strong-strong collision raises a candidate and merges nothing,
- two integration instances of the same provider with instance-scoped native ids and identical id
  values do **not** merge.

## 8. Non-goals, with revisit triggers

- **No canonical cross-provider property vocabulary.** Properties stay namespaced by provider
  (*unifi.name*, *fronius.name*); nothing is promoted to an unprefixed name and there is no
  precedence table. *Trigger:* a concrete consumer that cannot work with namespaced keys.
- **No automatic merge on weak claims, ever.** No trigger. This is a fixed decision.
- **No similarity-driven merge.** No trigger. Fixed.
- **No merge of an *Entity* into an integration-asserted element.** *Trigger:* a demonstrated case
  where a relation is genuinely insufficient, which would be a semantic-layer feature.
- **No observation-driven action.** An OUI vendor lookup saying a MAC belongs to a manufacturer is
  recorded as an **observation**, never an assertion. It may inform a suggestion in the UI; it may
  never trigger a merge, create a claim, or auto-configure anything. *Trigger:* none.
- **No un-merge.** A confirmed merge is a durable user assertion; reversing it is a manual claim
  removal. *Trigger:* users routinely confirming candidates by mistake.
- **No cross-namespace identity.** Claims resolve within one namespace. *Trigger:* a provider whose
  entities genuinely span namespaces.
