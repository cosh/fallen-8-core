# Async Completion Sweep — Plan

Single-phase, mechanical. See [spec.md](spec.md) for the inventory (A1–A6) and contracts.

## Phase 1 — convert, enforce, migrate

1. **Controllers** (`fallen-8-core-apiApp`):
   - `SubGraphController.CreateSubGraph`: `WaitUntilFinished()` → `await txInfo.Completion` (A1).
   - `SubGraphController.DeleteSubGraph`, `StoredQueriesController.RegisterStoredQuery`,
     `StoredQueriesController.DeleteStoredQuery`: `async Task<IActionResult>` + `await` (A2–A4).
   - `AnalyticsWriteBack.TryExecute(out,out,…)` → `ExecuteAsync(…) : Task<(Success, VerticesWritten,
     Chunks)>`; `AnalyticsController.ExecuteWriteBack` and `RunAnalytics` become async (A5).
   - `ScaleFreeNetwork.CreateScaleFreeNetwork` → `CreateScaleFreeNetworkAsync` (await vertex batch,
     collect per-partition edge `TransactionInformation`, `Task.WhenAll` their completions);
     `BenchmarkController.CreateGraph` becomes async (A6).
   - `TestGraphGenerator.GenerateSampleGraph`/`GenerateAbcGraph` → `…Async`;
     `SampleGraphController.CreateGraph` becomes `async Task` (A7).
2. **Convention test**: new `CodeQualityTest` rule — no `.WaitUntilFinished(` in
   `fallen-8-core-apiApp` outside `Services/DurabilityLifecycleService.cs`.
3. **Test migration**: append `.Result` at the call sites of the converted methods (the existing
   pattern for the already-async `CreateSubGraph`); `BenchmarkTest` waits on the renamed
   `CreateScaleFreeNetworkAsync`.
4. **Gates**: `dotnet build` (warnings-as-errors), full `dotnet test`, confirm the OpenAPI snapshot
   needs no regeneration (no route/doc change).

## Status

- [x] Phase 1 — all conversions done; convention rule active; suite green (846 passed);
  OpenAPI snapshot regenerated with zero diff.
