# Change Feed — Plan

Companion to [spec.md](./spec.md). A live SSE stream of committed graph mutations with
declarative server-side filtering and a bounded in-memory catch-up buffer. Feature branch:
`feature/change-feed` (branch-only workflow — no GitHub issue/PR).

Ordering principle: prove the engine-side pipeline (capture → sequence → ring → fan-out)
with its ordering/backpressure guarantees first, entirely under test, before any HTTP
surface; then the SSE endpoint + filter grammar; then catch-up semantics; Studio wiring
last (it consumes a finished contract). Every phase ends build-clean and tests-green.

## Phase 0 — Event model + descriptor capture on the write path

Intent: committed mutations become in-order descriptors on a channel; the writer is provably
untouched when the feed is off and unblocked when it is on.

- [ ] `fallen-8-core/ChangeFeed/`: `ChangeEventKind`, `ChangeEvent`, `ChangeDescriptor`
  (+ builder), `ChangeFeedOptions` — MIT headers, engine opt-in via the new
  `Fallen8(ILoggerFactory, WriteAheadLogOptions, ChangeFeedOptions, …)` surface mirroring
  the WAL options pattern (no options ⇒ no feed, zero cost).
- [ ] `ATransaction.DescribeChanges(ChangeDescriptor.Builder)` (internal virtual, default
  no-op) + overrides for every mutating transaction per the spec §3.3 table, including the
  `resync` reasons for Trim/TabulaRasa/Load/Delegate and cascade-removed edges.
- [ ] Hook in `TransactionManager`: capture in `ExecuteTransactionBody` after a successful
  `TryExecute` and **before** `ReleaseAfterCompletion`; publish in `FlushAndCompleteGroup`
  **after** the group fsync via non-blocking `TryWrite` into the bounded inbox channel;
  inbox overflow sets the lost-events flag. Contained like the existing `*Safely` helpers.
- [ ] Tests: rolled-back (clean + thrown) transactions capture nothing; each transaction
  type maps to exactly the specified descriptor content; the transaction-type completeness
  test (every concrete `ATransaction` mapped or explicitly exempted); publish order equals
  commit order under concurrent producers; feed-off engine has a null-check-only path.

## Phase 1 — Dispatcher, sequencing, ring buffer, subscriptions

Intent: the full in-process feed semantics — everything except HTTP.

- [ ] `ChangeFeedDispatcher`: single reader of the inbox; descriptor → per-element event
  expansion; monotonic `seq` assignment; per-process epoch GUID; append to
  `ChangeFeedRingBuffer` (configurable capacity, overwrite-oldest).
- [ ] `ChangeFeedFilter` (kind bitmask + label/key sets, AND-across / OR-within semantics,
  `resync` bypass) and `Subscribe/Unsubscribe` returning a per-subscriber bounded channel;
  `MaxSubscribers` enforcement; overflow → drop + single `resync{overflow}` marker;
  lost-events flag → `resync` to ring + all subscribers.
- [ ] Catch-up primitive: `Subscribe(filter, since)` replays ring events `> since` (filtered)
  then continues live gap-free; out-of-range / epoch-mismatch `since` ⇒ leading
  `resync{seekOutOfRange}`.
- [ ] Clean shutdown: dispatcher stops and drains inside engine `Dispose`.
- [ ] Tests: ordering (ascending seq, contiguous batches, consistent multi-subscriber
  views); filter matrix (each dimension, combinations, unlabeled elements, `keys` excluding
  non-property events, resync always delivered); ring wraparound with a small buffer;
  stalled-subscriber overflow (exactly one resync, fast subscriber unaffected); subscriber
  count lifecycle; dispose.

## Phase 2 — SSE endpoint + filter parsing + security surface

Intent: the HTTP contract, per repo controller conventions.

- [ ] `ChangeFeedController` (`GET api/v{version:apiVersion}/changefeed`): SSE writer
  (`text/event-stream`, `id: <epoch>:<seq>`, `event: <kind>`, `data: <json>`, keep-alive
  comments, response buffering off, flush per event), `RequestAborted` teardown.
- [ ] Query binding + validation: repeatable/CSV `kinds`/`elements`/`labels`/`keys`,
  `since` (`<epoch>:<seq>` or bare seq), `Last-Event-ID` honoured as `since`; invalid input
  → 400 problem+json; feed disabled → 503; `MaxSubscribers` exceeded → 503.
- [ ] `Fallen8:ChangeFeed:*` options bound + validated in the hosted app (hosted default
  `Enabled: true`), passed to the engine constructor next to the durability options;
  `appsettings.json` updated; OpenAPI annotations + regenerated pinned snapshot (additive).
- [ ] Auth: endpoint under the fallback policy (no `[AllowAnonymous]`); no key-in-query.
- [ ] Tests (`WebApplicationFactory`): end-to-end mutate→SSE-event round trip; SSE framing
  (id/event/data/keepalive); 400/401/503 matrix; kill switch off (`EnableDynamicCodeExecution
  =false`) with a filtered stream fully functional; payloads never contain property values;
  `Enabled: false` restores today's behaviour.

## Phase 3 — Non-regression benchmark + docs

Intent: prove requirement 2 with numbers, and document the contract.

- [ ] Opt-in benchmark (`[TestCategory("Benchmark")]` + `[Ignore]`, write-path-throughput
  harness style): committed tx/s on a WAL-enabled engine for feed-off /
  feed-on-no-subscriber / feed-on-one-subscriber / feed-on-with-stalled-subscriber; numbers
  recorded in this feature's README; feed-on within noise of feed-off.
- [ ] `features/open/change-feed/README.md`: endpoint usage (curl, native EventSource,
  fetch-based authenticated reader), filter grammar reference, the resync client recipe
  ("fetch, then stream; on resync, re-fetch"), config table, proxy/keep-alive deployment
  note.
- [ ] Root `README.md`: short "Live change feed" section.

## Phase 4 — Studio wiring (F8 Studio live mode)

Intent: the primary consumer stops polling. Consumes the finished contract only; can land
as a follow-up if the review prefers to gate the server side first.

- [ ] `fallen-8-web-ui`: `streamChanges` helper in the API client (fetch + SSE parser,
  existing auth headers, reconnect with last `id:` as `since`).
- [ ] Canvas/element-browser live updates per spec §3.7 (filter to on-screen labels/kinds;
  add/refresh/drop on events; mandatory resync handling re-fetching visible state; trim/
  tabula-rasa/load invalidate held ids); one stream per active instance (FR-1c isolation),
  torn down on instance switch; dashboard counters fed from the stream while connected.
- [ ] UI tests per the web-ui suite conventions (parser unit tests, resync handling,
  reconnect-with-since).

## Phase 5 — Gate

- [ ] Full `dotnet test` green; build 0 warnings/0 errors; benchmark numbers captured;
  manual smoke: `curl -N` against a running apiApp while mutating via Scalar.
- [ ] Council review per the repo merge gate; fix findings on the branch; `git merge
  --no-ff` to `main`; move `features/open/change-feed/` → `features/done/`.

## Progress

- [ ] Phase 0 — event model + writer-side capture/publish hook
- [ ] Phase 1 — dispatcher, sequencing, ring buffer, filters, backpressure
- [ ] Phase 2 — SSE endpoint, filter grammar, config, auth surface
- [ ] Phase 3 — throughput benchmark + docs
- [ ] Phase 4 — Studio live mode
- [ ] Phase 5 — council gate, merge + move to done/

## Decision / revisit conditions

- **Hook point** (capture pre-release on the writer, publish post-group-fsync, TryWrite
  only) is the load-bearing decision; any alternative that blocks or fans out on the writer
  reopens write-path-throughput and needs its benchmark re-run.
- **Metadata-only payloads** (no property values) revisit on a measured re-fetch bottleneck
  from a real consumer — the shape is an explicit `includeValues` opt-in.
- **In-memory-only catch-up** revisits when an external consumer needs resumption across
  restarts (replica/indexer): WAL-based replay + persisted sequence anchor.
- **SSE-only transport** revisits when a consumer needs client→server messages mid-stream
  (e.g. live filter changes without reconnect) — that is the WebSocket trigger.
- **No durable cursors / consumer groups / exactly-once** revisits only if multiple
  operator-managed external systems consume the feed and demonstrably miss events under the
  resync contract.
