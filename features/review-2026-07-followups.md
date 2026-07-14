# 2026-07 Architecture & Performance Review — Feature Index

This is the index of features created from the July 2026 principal-architect + performance review of
Fallen-8 (the goals under review: a fast in-memory graph DB, checkpointing/persistence, C# lambdas as
filters/path-patterns, and a first-class plugin story). Each entry below is a `features/<name>/`
folder with a `spec.md` + `plan.md`. They are independent units of work; the **Sequencing** section
records the order and dependencies.

Every spec was written against the current tree, so scope reflects what has already landed
(`transaction-failure-reasons`, `wal-subgraph-support`, `weighted-shortest-paths`,
`engine-performance` P1/P7, `collectible-codegen-assemblies`, the segmented store, adjacency
flattening, the hardened checkpoint format). Prior deferrals are honoured, not reopened:
`non-blocking-save` (measured, deferred), `csr-adjacency` (skipped), `engine-performance-followups`
P6 (parent-pointer BLS, deferred).

## Tier 1 — Correctness & durability (P0)

| Feature | What it fixes |
|---|---|
| [transaction-atomicity](transaction-atomicity/) | Enforce "RolledBack ⇒ zero observable effect". Batch creates can permanently break the `id == index` invariant (silent wrong-element lookups); batch remove/property report `RolledBack` while leaving partial mutations committed and unlogged. Construct-then-commit + validate-then-apply. |
| [load-path-integrity](load-path-integrity/) | Three checkpoint read/write cliffs: a load-time data race on the shared `edgeTodo` dictionary (silent adjacency loss/dup), a >2 GB sidecar silently overflowing an Int32 length header (saves fine, never loads), and an unbounded manifest-count preallocation (DoS). |
| [hosted-durability-lifecycle](hosted-durability-lifecycle/) | The hosted API never enables the WAL, never loads a checkpoint at startup, and never saves/flushes on shutdown — the server is volatile despite all the durability work. Add an `IHostedService` + WAL-enabling registration + config binding. |
| [api-security-boundary](api-security-boundary/) | The API is fully anonymous, including the endpoints that compile+execute arbitrary C# and upload+load DLLs — unauthenticated RCE. Add an authentication trust boundary, an operator opt-in kill switch for the dynamic-code/plugin surface, CORS, rate limiting, body-size limits, and a real loopback-by-default gate. |

## Tier 2 — Architectural risk & functional bugs (P1)

| Feature | What it fixes |
|---|---|
| [crash-durability-hardening](crash-durability-hardening/) | The WAL is correct when healthy and silent when it isn't: append-failure poisons the tail while later commits keep acking; a failed load runs an unlogged trim; a log adopted before its paired snapshot loads; inconsistent replay policy; non-durable rename commit points (no parent-dir fsync / `MOVEFILE_WRITE_THROUGH`); recipe manifest written after the commit point (now a live data-loss path since `wal-subgraph-support` landed). |
| [trim-reader-safety](trim-reader-safety/) | `Trim`/auto-trim renumbers live element ids in place under lock-free readers, and `VertexModel.GetHashCode == Id` — a reader-race + silent id-remap class. Identity-stable hash + tombstone body-freeing instead of hot-path renumbering + opt-in auto-trim. |
| [scan-result-representation](scan-result-representation/) | `GetAllVertices/Edges/GraphElements` (and `FindElementsIndex`) re-materialise an `ImmutableList` AVL tree per call (~158 MB measured for a 2.5M scan). Return array-backed `IReadOnlyList<T>`. |
| [supernode-adjacency-build](supernode-adjacency-build/) | Adjacency append is O(d) copy per edge → O(d²) to build/load a high-degree hub, with LOH churn. Batch-group wiring + amortised `(array, count)` capacity (the master-store discipline). |
| [index-lifecycle](index-lifecycle/) | Index membership is decoupled from element lifecycle, the single-writer thread, and the WAL: removed elements stay in (and are returned by) indices across four scan paths, index writes happen on the request thread, and buckets pin bodies. Route index writes through the pipeline; filter `_removed`; reverse-map removal. |
| [transaction-retention](transaction-retention/) | Transaction bookkeeping grows unbounded on insert-only workloads (~5 GB / 10M tx); `GetCreatedVertices` can throw after a concurrent `Cleanup`; `Error` is overloaded with durability-degraded. Bounded FIFO/TTL retention + a distinct `DurabilityDegraded` channel. |
| [dynamic-code-resource-limits](dynamic-code-resource-limits/) | Compiled filters have no compile bound and no execution budget (an infinite-loop filter hangs a thread forever), `MaxResults` (k) is unbounded, and arbitrary `Type.GetType(userString)` is a second code/side-effect surface across several endpoints. Add budgets + a primitive-type allow-list. |
| [api-error-contract](api-error-contract/) | Unhandled exceptions and swallowed errors contradict the documented status codes: `Convert.ToInt32` route ids → 500, `GetSource/TargetVertexForEdge` throw → 500 (documented 404), `/path` swallows compile failures into an empty 200, unbounded `maxElements`. Global `ProblemDetails` + `TryParse` + surfacing. |
| [path-filter-arity-fix](path-filter-arity-fix/) | The shipped default path filters (`(e,d)`, `(p,d)`) don't compile against the one-arg `Delegates.EdgeFilter`/`EdgePropertyFilter`, so any `/path` using default edge filters silently returns "no paths". Reconcile the contract; surface compile failure as 400; add the end-to-end codegen test that would have caught it. |
| [plugin-write-transactions](plugin-write-transactions/) | Plugins can read/derive but cannot mutate — `ATransaction` is `internal abstract`. A sanctioned `DelegateTransaction` running on the single writer thread, non-WAL-loggable by default (safe with zero WAL changes) or opt-in loggable. |

## Tier 3 — Performance (P2)

| Feature | What it improves |
|---|---|
| [write-path-throughput](write-path-throughput/) | WAL fsync-per-commit caps write throughput (~200–2000 tx/s) and `WaitUntilFinished` blocks request threads (the controllers are not actually async). Group commit + persistent append handle + genuinely-async awaitable completion + a timeout overload. |
| [traversal-allocations](traversal-allocations/) | BFS allocates ~2 objects + 3 lists per hop; Dijkstra re-fetches/re-sorts a vertex's neighbours per expansion; the public edge API allocates a wrapper per lookup. `readonly struct` frontier, per-search memoization, a `ReadOnlySpan<EdgeModel>` accessor. (Does not reopen the deferred `Path` rewrite.) |
| [checkpoint-io-efficiency](checkpoint-io-efficiency/) | Save/load read every sidecar twice for CRC with a byte-at-a-time CRC and a small buffer. Single-pass CRC (buffer + in-RAM on save, tee on load) + SIMD `Crc32` + `SequentialScan`. Also shrinks the measured save-stall without reopening `non-blocking-save`. |
| [codegen-cache-keying](codegen-cache-keying/) | The path-traverser compile cache keys on the whole `PathSpecification` though the artifact depends only on `(Filter, Cost)`, and rebuilds Roslyn metadata references on every compile. Narrow the key; hoist references to `static readonly`. |

## Recommended sequencing

1. **Correctness first:** `transaction-atomicity`, `load-path-integrity` — silent data-corruption bugs; everything else is secondary.
2. **Make the product match the codebase:** `hosted-durability-lifecycle` + `api-security-boundary` (shared `Program.cs`; do them together). This is what turns the durability/security work into real behaviour.
3. **Honest, fast durability:** `crash-durability-hardening`, then `write-path-throughput` (group commit builds on the failure-handling contract).
4. **Scale-hardening before any large/churn workload:** `trim-reader-safety`, `scan-result-representation`, `supernode-adjacency-build`, `index-lifecycle`, `transaction-retention`.
5. **Extensibility & API polish:** `path-filter-arity-fix` and `api-error-contract` (related), `dynamic-code-resource-limits` (with `api-security-boundary`), `plugin-write-transactions`, then the remaining perf items `traversal-allocations`, `checkpoint-io-efficiency`, `codegen-cache-keying`.

### Cross-feature coordination notes
- `hosted-durability-lifecycle` + `api-security-boundary` share `Program.cs`.
- `crash-durability-hardening`, `write-path-throughput`, and `index-lifecycle` all touch WAL entry-types / the codec — land the WAL-ordinal ownership once (`crash-durability-hardening`) and have the others build on it.
- `load-path-integrity` (L2) owns the `SavePartitions=5` default removal + <2 GB/bunch guard; `checkpoint-io-efficiency` references it rather than duplicating.
- `path-filter-arity-fix` (Phase 2) and `api-error-contract` both surface compile failures as 400 — coordinate the `/path` change.
- `supernode-adjacency-build`'s `(array, count)` change and `traversal-allocations`' span accessor both touch `EdgeAdjacency` — the count must be threaded through the ~7 internal consumers together.

> Provenance: created 2026-07-14 from the architecture & performance review. Each feature still needs
> its GitHub issue (label `feature`), branch (`feature/<name>`), and PR per the workflow in
> [../CLAUDE.md](../CLAUDE.md).
