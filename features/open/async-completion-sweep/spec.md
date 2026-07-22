# Async Completion Sweep — Specification

> **Status:** Open. Follow-up to [write-path-throughput](../../done/write-path-throughput/spec.md),
> which built the awaitable primitive (`TransactionInformation.Completion`, completed strictly after
> the group fsync) and converted `GraphController`'s five mutations plus `AdminController.Load`/`Save`
> — but deferred the remaining conversions. Since then, `stored-query-library` and `graph-analytics`
> added NEW blocking waits on request paths, so the fake-async pattern crept back. This sweep finishes
> the conversion and adds the convention test that keeps it finished.

## 1. Problem / current state

`TransactionInformation.WaitUntilFinished()` is a blocking `Task.Wait()`. On an ASP.NET request
path it pins a thread-pool thread for the transaction's full queue latency — the exact thread-pool
starvation risk write-path-throughput fixed for the high-frequency mutations. These request paths
still block:

| # | Call site | Shape |
|---|-----------|-------|
| A1 | `SubGraphController.CreateSubGraph` | **Fake async**: `async Task<IActionResult>` signature, but blocks on `WaitUntilFinished()` inside |
| A2 | `SubGraphController.DeleteSubGraph` | Synchronous action, blocking wait |
| A3 | `StoredQueriesController.RegisterStoredQuery` | Synchronous action, blocking wait (landed after the sweep) |
| A4 | `StoredQueriesController.DeleteStoredQuery` | Synchronous action, blocking wait (landed after the sweep) |
| A5 | `AnalyticsController.RunAnalytics` → `AnalyticsWriteBack.TryExecute` | Synchronous action; the write-back blocks once per 50k-vertex chunk |
| A6 | `BenchmarkController.CreateGraph` → `ScaleFreeNetwork.CreateScaleFreeNetwork` | Synchronous action; blocks on the vertex batch and once per edge partition |
| A7 | `SampleGraphController.CreateGraph` → `TestGraphGenerator.GenerateSampleGraph`/`GenerateAbcGraph` | Synchronous action; the generators block on their vertex and edge batches |

Not in scope (documented, deliberate blocking):

- `DurabilityLifecycleService.StartAsync`/`StopAsync` — host lifecycle, runs once at startup and
  shutdown, never on a request thread. Allowlisted in the convention test.
- Engine-internal waits (`BreadthFirstSearchSubgraphAlgorithm`, `Fallen8.Persistence`) — writer-thread
  mechanics against subordinate engines, not request paths. The engine keeps the synchronous
  `WaitUntilFinished()` contract; this sweep touches only `fallen-8-core-apiApp`.

## 2. Goals / non-goals

**Goals.**
- Every request-path wait on a transaction outcome `await`s `Completion` instead of blocking.
- A `CodeQualityTest` convention rule fails the suite on any future `WaitUntilFinished(` in
  `fallen-8-core-apiApp` outside the documented allowlist — the pattern regressed once
  (stored-query-library) and a prose rule demonstrably does not hold it.
- Status codes, response bodies, failure-reason mapping, and OpenAPI surface are byte-identical.

**Non-goals.**
- No engine (`fallen-8-core`) changes. `WaitUntilFinished()` stays for engine internals, hosted
  lifecycle, and tests; `Completion` already exists.
- No async-ification of reads. Reads are lock-free, CPU-bound, in-memory — there is nothing to
  await; `Task<T>` wrappers there would be pure overhead.
- No reopening of `non-blocking-save` (measured, deferred): the Save/serialize stays on the writer.

## 3. Design

- **A1** `CreateSubGraph`: replace the blocking wait with `await txInfo.Completion;` — one line.
- **A2–A4**: actions become `async Task<IActionResult>`; the wait becomes `await txInfo.Completion;`.
  Everything else (up-front checks, failure-reason mapping, status codes) is unchanged.
- **A5**: `out` parameters cannot cross `await`, so `AnalyticsWriteBack.TryExecute(out, out, …)`
  becomes `ExecuteAsync(…) : Task<(Boolean Success, Int32 VerticesWritten, Int32 Chunks)>`. Chunks
  stay strictly sequential (chunk N+1 is enqueued only after N committed) so the stop-on-first-
  rollback contract and the documented chunk-atomic/non-run-atomic semantics are untouched.
  `RunAnalytics` becomes async; `AnalyticsRunGate` is a `SemaphoreSlim` (no thread affinity), so
  the existing `TryEnter`/`finally Exit` bracket is safe across `await`.
- **A6**: `CreateScaleFreeNetwork` becomes `CreateScaleFreeNetworkAsync`. The vertex batch is
  awaited. Edge building keeps `Parallel.ForEach` for the CPU-bound transaction construction, but
  each partition now returns its `TransactionInformation` instead of blocking inside the loop; the
  method awaits all edge completions afterwards (`Task.WhenAll`). Net effect: the ~cores edge
  transactions are in flight together — a better group-commit workload — and no pool thread blocks.
- **A7**: the two generators become `GenerateSampleGraphAsync`/`GenerateAbcGraphAsync` (awaiting
  their vertex/edge batches); `SampleGraphController.CreateGraph` becomes `async Task`.
- **Convention test**: `CodeQualityTest` scans `fallen-8-core-apiApp/**/*.cs` (comments stripped)
  for `.WaitUntilFinished(`; allowlist: `Services/DurabilityLifecycleService.cs`.

## 4. Acceptance criteria

- No `WaitUntilFinished(` remains in `fallen-8-core-apiApp` outside the allowlist; the new
  convention rule enforces it and fails with the violating files listed.
- All converted endpoints return the same status codes for the same outcomes (the existing
  controller tests pin these; they are migrated mechanically to `.Result` at the call sites,
  matching how the already-async `CreateSubGraph` is called today).
- The OpenAPI snapshot is unchanged (routes, XML docs, and response types are untouched).
- Full suite green; warnings-as-errors build clean.

## 5. Impact on existing features

- **REST contract / OpenAPI snapshot** — unchanged: return-type `Task<IActionResult>` vs
  `IActionResult` is invisible to the OpenAPI document; no route, doc, or response-type edits.
- **Studio UI / NL-assist dataset** — unaffected (no contract change, no retrain entry needed).
- **write-path-throughput** — this sweep completes that spec's deferred item (c); its spec is a
  historical record and is not rewritten.
- **stored-query-library / graph-analytics / subgraph** — behaviour identical; their READMEs do not
  describe the blocking-vs-await mechanics, so no doc updates are needed.
- **Persisted assets (save games, WAL, recipes, stored queries)** — untouched.
