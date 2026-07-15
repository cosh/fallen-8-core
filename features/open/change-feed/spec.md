# Change Feed — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/change-feed` (branch-only workflow —
> no GitHub issue/PR).
>
> **Primary consumer:** F8 Studio ([web-ui](../../done/web-ui/spec.md)). The web UI spec
> explicitly parked "change streaming (the API has no notification channel; see gap G-5)" —
> this feature closes that gap: live graph updates instead of polling.

## 1. Overview & requirements

Fallen-8 has no way to tell a client that the graph changed. F8 Studio polls (`GET /status`
counts, re-fetching elements); any external consumer that wants to mirror or react to
mutations has to diff scans. This feature adds a **live change feed**: a stream of committed
graph mutations, exposed over HTTP as **Server-Sent Events (SSE)**, with **server-side
declarative filtering** and a bounded in-memory **catch-up buffer**.

Requirements fixed up front:

1. **Post-commit only.** Events are emitted on the write pipeline *after* the commit point —
   after the transaction is applied and after the commit group's WAL fsync — so a subscriber
   never observes a mutation that could still roll back or that a waited-on writer has not yet
   been acked for.
2. **The feed must not regress write throughput.** The single writer thread
   (`TransactionManager`, feature [write-path-throughput](../../done/write-path-throughput/spec.md):
   group commit, 17k writes/s measured) never blocks on, waits for, or fans out to
   subscribers. Emission is a fire-and-forget `TryWrite` into a bounded channel.
3. **Filtering is a hard requirement, and it is declarative.** Filters are query parameters
   (kind / element type / label / property key), **not** compiled C# fragments. The feed must
   work with the dynamic-code kill switch (`Fallen8:Security:EnableDynamicCodeExecution =
   false`, the [api-security-boundary](../../done/api-security-boundary/spec.md) default) left
   off, and a long-lived stream is exactly the wrong place to keep user-supplied code hot.
4. **Right-sized.** Fallen-8 is a single-process, self-hosted, single-operator database. No
   broker, no per-consumer durable cursors, no consumer groups, no exactly-once claims. The
   delivery contract is: *in-order, at-most-once per connection, with an explicit `resync`
   signal whenever continuity was lost* — the client's recovery action is always "re-fetch
   what you care about", which Studio has to be able to do anyway.

## 2. Goals / non-goals

**Goals**

- A **`ChangeFeed` component in `fallen-8-core`** (new folder `fallen-8-core/ChangeFeed/`):
  event model, monotonic sequencing, a bounded ring buffer for catch-up, and a dispatcher
  that fans out to per-subscriber bounded queues. Activated by a `ChangeFeedOptions`
  constructor parameter, mirroring the `WriteAheadLogOptions` opt-in pattern; an engine
  constructed without it carries no feed and pays nothing.
- A **hook at the group-commit point** in `TransactionManager`: descriptors captured on the
  writer at execute time, published after the group fsync, never blocking (§3.2).
- **`GET /api/v{version}/changefeed`** in `fallen-8-core-apiApp` (new `ChangeFeedController`,
  standard `[Route("api/v{version:apiVersion}/[controller]")]` + `[ApiVersion("0.1")]`
  conventions): SSE stream with filter query parameters, `?since=` catch-up, keep-alive
  comments, and honest OpenAPI annotations.
- **Auth included for free:** the endpoint sits behind the existing `FallbackPolicy`
  (API key / bearer, api-security-boundary) like every other endpoint — no `[AllowAnonymous]`.
- **Coarse `resync` events** for the operations whose effect cannot be expressed as element
  deltas: `Trim` (id reassignment), `TabulaRasa`, `Load`, and `DelegateTransaction` (opaque
  plugin writes). A client that receives `resync` re-fetches its visible state.
- **Configuration** under `Fallen8:ChangeFeed:*` in `appsettings.json`, bound by the hosted
  app; **enabled by default in the hosted API** (read-only surface, small idle cost, and it
  is what makes Studio live out of the box), engine-level opt-in via options.
- **MSTest coverage** at the repo bar: ordering, filter semantics per dimension and combined,
  ring wraparound + resync, slow-consumer overflow, lifecycle, auth/kill-switch interaction,
  and an opt-in throughput benchmark pinning non-regression.

**Non-goals** (each with its named revisit trigger)

- **WebSocket transport.** SSE covers server→client streaming, reconnect, and last-event-id
  natively. *Revisit trigger:* a consumer needs client→server messages mid-stream (e.g.
  changing its filter without reconnecting).
- **WAL-file replay / catch-up across restarts.** `?since=` is served from the in-memory ring
  only; a restart is a `resync`. *Revisit trigger:* an external consumer (replica, secondary
  indexer) needs to resume across process lifetimes — that consumer's arrival is the trigger,
  and the shape is WAL-based replay plus a persisted sequence anchor.
- **Per-consumer durable cursors, consumer groups, exactly-once delivery.** Kafka-shaped
  machinery for a problem this deployment does not have. *Revisit trigger:* more than one
  operator-managed external system consumes the feed and demonstrably misses events under the
  resync contract.
- **Property values in event payloads.** Events carry the property *key*, never the value
  (§3.3 rationale). *Revisit trigger:* a real consumer measures the re-fetch round-trip as a
  bottleneck; the shape is an explicit `includeValues=true` opt-in.
- **Compiled C# filter fragments on the feed.** Contradicts requirement 3. *Revisit trigger:*
  a concrete filter need that the declarative grammar cannot express **and** a deployment
  that runs with dynamic code enabled anyway.
- **Transaction envelopes / atomic event grouping.** Events do not carry a transaction id and
  are not framed per transaction. *Revisit trigger:* a consumer needs to apply a
  transaction's events atomically.
- **Cluster / replication fan-out.** Single-node only, like everything else.

## 3. Design sketch

### 3.1 Where the hook sits (and why it cannot slow the writer)

The write pipeline today (`TransactionManager.ConsumeLoop`, single writer thread):

```
drain queue → ExecuteTransactionBody (per tx: TryExecute, terminal state, buffer WAL frame,
release inputs, auto-trim) → FlushAndCompleteGroup (ONE fsync, then complete every TCS)
```

The feed adds two touches, both on the writer, both cheap and non-blocking:

1. **Capture** — in `ExecuteTransactionBody`, immediately after a successful `TryExecute` and
   **before** `ReleaseAfterCompletion()` (which drops the input payload), the writer asks the
   transaction for a compact **`ChangeDescriptor`** via a new
   `internal virtual void DescribeChanges(ChangeDescriptor.Builder)` on `ATransaction`
   (default: no-op). The descriptor holds only ids, labels, property keys, edge endpoints,
   and one commit timestamp — no property values, no model references — and is stored on the
   group's `WorkItem`. Rolled-back transactions capture nothing (atomicity contract:
   `RolledBack` ⇒ zero observable effect ⇒ zero events).
2. **Publish** — in `FlushAndCompleteGroup`, **after** the group fsync, the writer
   `TryWrite`s each `Finished` member's descriptor into a single bounded
   `Channel<ChangeDescriptor>` (the dispatcher inbox), in commit order, then completes the
   completion sources as today. `TryWrite` on a bounded channel with `DropWrite` semantics
   never blocks: if the inbox is full (the dispatcher is stalled), the descriptor is dropped
   and a **lost-events flag** is set; the dispatcher turns that flag into a `resync` for the
   ring buffer and every subscriber. The writer is never delayed, never faulted (the publish
   sits inside the same containment discipline as `BufferCommittedTransactionSafely`).

Why this point: after the fsync is the durable-before-ack boundary — an event published here
describes state that every reader (`IFallen8Read`) already sees and that a waited-on caller
is about to be acked for. Publishing earlier (post-execute, pre-fsync) would let a subscriber
act on a commit whose ack is still pending; publishing later (off the writer entirely, by
diffing) would be a different, far more expensive feature. When the WAL is off (volatile
opt-out), the fsync is a no-op and "post-commit" means post-apply, exactly matching what
reads see. When a group flush fails (`Durable == false`, crash-durability-hardening D1), the
transaction is still committed in memory and readers see it — so the feed publishes it too;
the feed mirrors committed state, it is not a durability signal.

Cost accounting for the non-regression argument: with the feed **disabled** (no options), the
hook is a null check per group. With the feed **enabled**, the per-committed-transaction cost
is one small descriptor allocation plus one channel `TryWrite` — no fsync added, no lock on
the writer path, no per-subscriber work on the writer (fan-out happens on the dispatcher).
The benchmark in §4 pins this.

### 3.2 Sequencing, ring buffer, dispatcher

A single background **dispatcher** task (started with the feed, stopped on engine dispose) is
the only reader of the inbox channel. Per descriptor, in arrival order (= commit order, since
the writer is the only producer), it:

1. **Expands** the descriptor into per-element `ChangeEvent`s (a `CreateVerticesTransaction`
   with N vertices becomes N `vertexCreated` events, contiguous).
2. **Assigns** each event a monotonic `long seq`, starting at 1 per process. The feed also
   owns a per-process **epoch id** (a GUID minted at feed construction) so a client cannot
   mistake a post-restart seq 4711 for the pre-restart one.
3. **Appends** the event to the **ring buffer** (capacity `BufferSize`, default 8192,
   overwriting oldest; a short lock guards append/replay — never touched by the writer).
4. **Fans out** to each live subscriber: applies the subscriber's compiled filter (§3.4) and
   `TryWrite`s into the subscriber's own bounded channel. Overflow handling per §3.5.

`resync` events (from Trim/TabulaRasa/Load/DelegateTransaction descriptors, from the
lost-events flag, or synthesized at subscribe time) get sequence numbers and enter the ring
buffer like any other event, so a replay from `?since=` reproduces them in order.

Ordering guarantee stated plainly: **all subscribers observe the same events in the same
total order (ascending seq), which is commit order.** Filtering removes events from a
subscriber's view; it never reorders them.

### 3.3 Event model & JSON schema

Kinds (`ChangeEventKind`): `vertexCreated`, `vertexRemoved`, `edgeCreated`, `edgeRemoved`,
`propertySet`, `propertyRemoved`, `resync`.

Mapping from the write surface (the same operations the WAL logs, `WalEntryType`):

| Transaction | Events |
|---|---|
| `CreateVertexTransaction` / `CreateVerticesTransaction` | `vertexCreated` per vertex (the batch transaction creates vertices only) |
| `CreateEdgeTransaction` / `CreateEdgesTransaction` | `edgeCreated` per edge |
| `AddPropertyTransaction` / `AddPropertiesTransaction` | `propertySet` per APPLIED (element, key) — a no-op target emits nothing |
| `RemovePropertyTransaction` | `propertyRemoved` iff a property was actually removed |
| `RemoveGraphElementTransaction` / `RemoveGraphElementsTransaction` | `vertexRemoved` / `edgeRemoved` per element actually removed (edges removed by cascade included, deduplicated) |
| `CreateSubGraphTransaction` / `RemoveSubGraphTransaction` | nothing — a subgraph is derived state materialized in its OWN standalone graph instance; the main graph is not mutated |
| `RegisterStoredQueryTransaction` / `RemoveStoredQueryTransaction` | nothing (library state, not graph state) |
| `TrimTransaction` | `resync` (`reason: "trim"`) — ids are reassigned; every client-held id is invalid |
| auto-trim | nothing — automatic reclamation is renumber-free (trim-reader-safety): it frees the bodies of already-removed elements without any observable change |
| `TabulaRasaTransaction` | `resync` (`reason: "tabulaRasa"`) |
| `LoadTransaction` | `resync` (`reason: "load"`) — state was replaced wholesale |
| `SaveTransaction` | nothing (no graph mutation) |
| `DelegateTransaction` | `resync` (`reason: "delegateWrite"`) — plugin mutations are opaque to the descriptor model |

JSON schema (one object per SSE `data:` line; fields absent rather than null when not
applicable):

```json
{
  "seq": 4712,                       // long, monotonic per process epoch, gap-free per stream
  "ts": "2026-07-15T12:34:56.789Z",  // commit timestamp (UTC, ISO-8601), captured once per
                                     // transaction on the writer; shared by its events
  "kind": "propertySet",             // one of the kinds above
  "element": "vertex",               // "vertex" | "edge" — element events only
  "id": 42,                          // element id — element events only
  "label": "person",                 // element label; omitted when the element has none
  "key": "name",                     // property key — propertySet/propertyRemoved only
  "source": 7,                       // source vertex id — edgeCreated only
  "target": 9,                       // target vertex id — edgeCreated only
  "reason": "trim"                   // resync only: "trim" | "tabulaRasa" | "load" |
                                     // "delegateWrite" | "overflow" | "seekOutOfRange"
}
```

**Property values are deliberately elided.** Rationale, stated honestly: (a) *payload &
memory* — values are arbitrarily large objects, and every event is held once in the ring
buffer plus once per subscriber queue; copying values multiplies that by fan-out. (b)
*security posture* — the feed is a long-lived push channel; keeping it metadata-only means a
subscriber authorized to "know that something changed" does not automatically receive every
written value, and the payload can never leak data the client would not have fetched. The
consumer re-fetches the element (`GET /graphelement/{id}`) when it needs the value — Studio
does that anyway to get typed properties. Values-on-request is a parked non-goal (§2).

### 3.4 Transport: SSE endpoint & filter grammar

`GET /api/v{version:apiVersion}/changefeed` on the new `ChangeFeedController`
(`Produces("text/event-stream")`, `[ProducesResponseType]` for 200/400/401/503, XML
`<summary>`/`<remarks>` per repo convention). Response is an unbounded SSE stream:

```
id: 0b1e4c…-…:4712        ← "<epoch-guid>:<seq>"
event: propertySet
data: {"seq":4712,"ts":"…","kind":"propertySet","element":"vertex","id":42,"label":"person","key":"name"}

: keepalive                ← comment line every KeepAliveSeconds (default 15)
```

- `id:` carries `<epoch>:<seq>` so a native `EventSource` auto-reconnect sends a
  `Last-Event-ID` header the server can resume from — including detecting a restart (epoch
  mismatch ⇒ `resync`).
- Keep-alive comments bound dead-connection detection and keep proxies from idling out the
  stream. Disconnects are observed via `HttpContext.RequestAborted`; the subscription is
  unregistered and its queue dropped.
- Response buffering is disabled for the stream; each event is flushed as written.

**Query parameters.** All filter parameters are optional; an unset dimension is a wildcard.
Every parameter may be repeated and each occurrence may be a comma-separated list; the
effective set is the union of all values. Matching is ordinal and case-sensitive (label/key
semantics match the engine's).

```
changefeed?kinds=…&elements=…&labels=…&keys=…&since=…

kinds    : subset of { vertexCreated, vertexRemoved, edgeCreated, edgeRemoved,
                       propertySet, propertyRemoved }
elements : subset of { vertex, edge }
labels   : element labels (exact match)
keys     : property keys (exact match)
since    : "<epoch>:<seq>" (as emitted in id:) or a bare seq (assumed current epoch)
```

**Filter semantics (exact).** An element event passes a filter iff, for **each dimension the
caller set**, the event *carries* that attribute and the attribute's value is in the set
(logical AND across dimensions, OR within a dimension). Consequences, spelled out:

- `kinds=propertySet` — only property-set events.
- `keys=name` — only property events (they alone carry `key`) whose key is `name`; setting
  `keys` therefore excludes creates/removes. A client that wants both subscribes twice (e.g.
  one connection with `kinds=vertexCreated,vertexRemoved&labels=person`, one with
  `keys=name`) — subscriptions are cheap by design.
- `labels=person&elements=vertex` — vertex events whose label is exactly `person`; an
  unlabeled element never matches a `labels` filter.
- `resync` events **bypass all filters** — continuity loss must reach every subscriber; a
  filter that could suppress `resync` would silently corrupt the client's view. (`resync`
  is accepted in `kinds` for symmetry but is a no-op there.)
- Unknown values in `kinds`/`elements`, or a malformed `since`, are a **400** problem+json
  (api-error-contract style), never a silently-empty stream.

At subscribe time the filter compiles to a kind bitmask plus `HashSet<string>`s (or
`FrozenSet`) for labels/keys — per-event cost per subscriber is a few set lookups, so many
subscribers with different filters stay cheap. `MaxSubscribers` (default 32) bounds the
count; a subscribe beyond it is **503** with a clear message.

**Auth note (honest).** The endpoint inherits the fallback policy, so it needs the API key /
bearer header like everything else. The browser-native `EventSource` constructor cannot set
custom headers; that is fine for the no-auth loopback developer default, but authenticated
Studio consumes the stream with `fetch()` + a small SSE reader over `ReadableStream` (same
wire format, headers available — Studio already routes every call through its client with
auth headers). We explicitly do **not** add an API-key-in-query-string fallback: keys in URLs
leak into logs and proxies.

### 3.5 Catch-up (`since`) and backpressure

**Catch-up.** On connect with `since=<epoch>:<seq>`:

- Epoch matches and `seq ≥` oldest buffered seq: replay the buffered events with
  `seq > since` (filtered), then continue live with no gap (the subscription registers before
  replay so nothing falls between replay and live).
- Epoch matches but `seq <` oldest buffered (fell out of the ring) or `>` head (nonsense):
  the stream starts with `resync` (`reason: "seekOutOfRange"`), then live events.
- Epoch mismatch or no `since`: no replay; a fresh subscription starts at the live head (a
  client that needs current state fetches it via the REST reads first, then relies on the
  feed — the `resync` contract makes "fetch, then stream" the universal client recipe).

**Backpressure / slow consumers.** Each subscriber owns a bounded channel
(`SubscriberQueueSize`, default 1024). The dispatcher's `TryWrite` never blocks: on a full
queue the dispatcher **stops enqueueing for that subscriber, marks it overflowed, and
enqueues a single `resync` (`reason: "overflow"`) as soon as space frees** (drop + resync
marker). One slow consumer therefore costs everyone else nothing and costs the writer
nothing; the slow consumer keeps its connection and is told, in-band, that it must re-fetch.
The same pattern covers the writer→dispatcher inbox (§3.1): inbox overflow becomes a
`resync` to the ring and all subscribers.

### 3.6 Configuration

```jsonc
"Fallen8": {
  "ChangeFeed": {
    "Enabled": true,            // hosted default ON (read-only, cheap); engine default OFF
    "BufferSize": 8192,         // ring-buffer capacity (events)
    "SubscriberQueueSize": 1024,// per-subscriber bounded queue (events)
    "MaxSubscribers": 32,       // concurrent SSE connections; beyond -> 503
    "KeepAliveSeconds": 15      // SSE comment heartbeat
  }
}
```

Bound to a validated options type in the hosted app (hosted-durability-lifecycle pattern) and
passed to the engine as `ChangeFeedOptions` at construction, next to `WriteAheadLogOptions`.
`Enabled: false` reverts the hosted app to today's behaviour exactly (endpoint answers 503
"change feed disabled" — or is absent from routing; pick 503 for a stable OpenAPI surface —
and the engine hook is a null check).

### 3.7 Studio integration (spec-level; implementation is a follow-up phase)

- Studio's API client gains a `streamChanges(filter, since, onEvent, onResync)` helper built
  on `fetch()` + SSE parsing, carrying the instance's auth headers like every other call.
- **Canvas/browser live mode:** subscribe filtered to what is on screen (the labels present
  on the canvas, `elements` per view); `vertexCreated`/`edgeCreated` matching the filter →
  fetch the element and add/refresh; `*Removed` → drop it from the canvas;
  `propertySet`/`propertyRemoved` on a displayed element → re-fetch that element's detail.
- **Resync handling is mandatory, not optional:** on any `resync`, Studio re-fetches the
  visible state (the same fetches the screen issued to render initially) and, for
  `reason: "trim"`/`"tabulaRasa"`/`"load"`, treats all held ids as invalid.
- Reconnect uses the last seen `id:` as `since`; per-instance state isolation (web-ui FR-1c)
  applies — one stream per active instance, torn down on instance switch.
- Dashboard counters update from the feed instead of polling `/status` while a stream is up.

## 4. Acceptance criteria

All tests MSTest in `fallen-8-unittest`, arrange/act/assert, `TestLoggerFactory.Create()`.

- **Post-commit only.** A waited-on committed write's event is observable on a subscription;
  a rolled-back transaction (clean rollback and thrown-then-rolled-back) emits **zero**
  events; with the WAL on, no event for a transaction is delivered before that transaction's
  `Completion` could observe durable-before-ack (pinned by hook placement + an ordering test).
- **Ordering.** Under concurrent producers, every subscriber sees strictly ascending `seq`
  with commit order preserved; a batch transaction's events are contiguous; two subscribers
  with different filters see consistent (subset-of-the-same-total-order) views.
- **Event mapping.** Each transaction row in §3.3 produces exactly the listed events with the
  specified fields (including `source`/`target` on `edgeCreated`, cascade-removed edges on
  vertex removal, and `resync` reasons for trim/tabula-rasa/load/delegate).
- **Filter semantics.** Per-dimension tests plus combination tests (AND across dimensions, OR
  within); `keys` excluding non-property events; unlabeled elements vs `labels`; `resync`
  always delivered; unknown kind/element and malformed `since` → 400 problem+json.
- **Catch-up.** `since` inside the ring replays exactly the missed events then continues live
  gap-free; `since` older than the ring → leading `resync{seekOutOfRange}`; epoch mismatch
  (simulated restart) → `resync`; ring wraparound (BufferSize exceeded) verified with a small
  configured buffer.
- **Backpressure.** A deliberately stalled subscriber overflows its queue → it receives
  exactly one `resync{overflow}` (no duplicate storm) and resumes live; a concurrent fast
  subscriber misses nothing; write throughput during the stall is unaffected (asserted via
  the benchmark harness); the writer thread never blocks (no fan-out on the writer).
- **Lifecycle.** Disconnect unregisters the subscription (observed via subscriber count);
  `MaxSubscribers + 1` → 503; engine dispose stops the dispatcher cleanly.
- **Security surface.** No API key → 401 on the endpoint; the feed works fully with
  `EnableDynamicCodeExecution=false` (a filtered subscription over a mutation workload);
  event payloads never contain property values.
- **Throughput non-regression.** An opt-in benchmark (`[TestCategory("Benchmark")]` +
  `[Ignore]`, mirroring the write-path-throughput harness) compares committed tx/s for
  feed-off / feed-on-no-subscriber / feed-on-one-subscriber on a WAL-enabled engine; feed-on
  is within noise of feed-off (the report records the numbers).
- **Suite green, build clean**; with `ChangeFeed:Enabled=false` the hosted app behaves
  exactly as today.

## 5. Risks

- **Writer-path creep (highest).** Any blocking call, lock contention, or fan-out sneaking
  onto the writer thread undoes write-path-throughput. Mitigation: the writer touches only
  descriptor capture + `TryWrite`; fan-out and filtering live on the dispatcher; the
  benchmark pins it.
- **Descriptor capture vs input release.** Capture must happen before
  `ReleaseAfterCompletion()` drops the payload, and must copy primitives (ids/labels/keys)
  rather than retain model or definition references — otherwise the feed re-introduces the
  M3 memory retention the release exists to fix.
- **Missed-continuity bugs** (an event dropped without a `resync`) are silent client
  corruption. Every drop path — inbox overflow, subscriber overflow, ring eviction past a
  `since`, epoch change — must emit `resync`; each path has a dedicated test.
- **SSE through proxies/HTTP.2 quirks:** buffering middleware or proxy idle timeouts can
  stall or kill streams. Keep-alive comments + disabling response buffering mitigate;
  documented as a deployment note (same posture as TLS: the proxy recipe, not in-app
  machinery).
- **Unbounded connection lifetime** holds a request + subscriber queue per client;
  `MaxSubscribers` and the bounded queues cap memory; `RequestAborted` cleanup is tested.
- **Semantic drift between feed and WAL** (a new mutating transaction added later that
  forgets `DescribeChanges`). Mitigation: a test enumerating the concrete `ATransaction`
  types asserts each is either mapped in §3.3 or explicitly exempted (`SaveTransaction`),
  so a new transaction type fails the suite until classified.

## 6. Keep (do not regress)

- **Single-writer model and group commit** (write-path-throughput): one writer thread, one
  fsync per group, completion strictly after the fsync, serial-producer latency unchanged.
- **Durable-before-ack** and the D1/D3 degraded-durability semantics
  (crash-durability-hardening): the feed observes them; it must not alter when completions
  fire or what `Durable` means.
- **Transaction atomicity:** `RolledBack` ⇒ zero observable effect now also means zero
  feed events.
- **The security posture** (api-security-boundary): fallback auth applies to the endpoint;
  the kill switches stay off by default and the feed never depends on them; no credential
  ever moves into a query string.
- **Hosted lifecycle** (hosted-durability-lifecycle): load-on-boot / save-on-shutdown
  ordering is untouched; the dispatcher stops cleanly inside the existing dispose path.
- **WAL format and replay:** the feed writes nothing to disk; `WalEntryType` and the codec
  are unchanged.
- **The repo's API conventions:** versioned route, `ProducesResponseType`, problem+json
  errors, OpenAPI surfacing; and the pinned OpenAPI snapshot gains the endpoint additively.
