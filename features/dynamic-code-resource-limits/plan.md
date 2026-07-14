# Dynamic-Code Resource Limits & Type Allow-List — Plan

Companion to [spec.md](./spec.md). Ordered by value-per-risk: the type allow-list (smallest, closes a
read-path code-exec surface) first, then compile bounds, then the execution budget (largest). Every
limit is configurable with a safe default and effectively-unlimited when unset, mirroring
[../subgraph-quotas/](../subgraph-quotas/).

GitHub issue: to be opened (label: `feature`).

## Phase 0 — Baseline & guardrails
Intent: prove each vulnerability with a test that guards the fix, without letting the suite itself
hang or force-load anything permanently.
- [ ] Characterization test: `/path` (or `WeightedDijkstraShortestPath` directly) with a
  `return (v) => { while(true){} };` filter does **not** complete inside a short wall-clock deadline
  today — run the traversal under `Task.Run` with the test owning a hard `CancellationTokenSource`
  timeout so CI never hangs; assert the task is still running at the deadline (documents R1).
- [ ] Characterization test: a fragment far longer than any sane filter compiles today with no
  rejection (documents R2).
- [ ] Characterization test: a `FullQualifiedTypeName` naming a type with an observable static-ctor
  side effect is resolved via `Type.GetType` today (documents R3); assert the side effect fires.
- [ ] Opt-in `[TestCategory("Benchmark")]` + `[Ignore]` micro-benchmark measuring Roslyn compile cost
  for a small vs. large fragment (numbers to be captured on this box), to guard the compile bound.
- [ ] Tests use MSTest arrange/act/assert and `TestLoggerFactory.Create()`.

## Phase 1 — Type allow-list (R3)
Intent: no untrusted string ever reaches `Type.GetType`.
- [ ] Add `AllowedLiteralTypes.TryResolve(string name, out Type type)` — static, case-insensitive map
  of the primitives from spec §3(C), keyed by full name + short alias.
- [ ] Replace every `Type.GetType(userString, true, true)` site: `GraphController.GraphScan`
  (`:503`), `IndexScan` (`:552`), `RangeIndexScan` (`:598`/`:601`), `AddProperty` (`:738`); and
  `ServiceHelper.CreateObject` (`:73`), both `GenerateProperties` overloads (`:95`/`:96`, `:123`/`:124`)
  and `Transform` (`:138`). Miss ⇒ reject with `400` (controllers) / `Try*`-style failure (helpers),
  never call `Type.GetType`.
- [ ] Tests: non-allow-listed name → `400` and no assembly load / no static-ctor side effect; each
  accepted primitive still round-trips on the scan and property endpoints; null `FullQualifiedTypeName`
  keeps the current "use the raw value" behaviour.

## Phase 2 — Compile bounds (R2)
Intent: bound compile CPU/memory before Roslyn runs.
- [ ] Add `DynamicCodeLimits` (`MaxFilterFragmentLength`, `MaxGeneratedSourceLength`, `CompileTimeout`)
  with safe defaults; thread it into `CodeGenerationHelper` (static entry points take the limits, or a
  configured singleton).
- [ ] In `GeneratePathTraverser`/`CreateSource` and `CompileDelegates`/`BuildProviderSource`: reject an
  over-length fragment or generated source up front, returning the existing error-message string.
- [ ] Pass a `CancellationTokenSource(CompileTimeout).Token` into `ParseSyntaxTree(…, ct)` and
  `compilation.Emit(ms, cancellationToken: ct)` at `CodeGenerationHelper.cs:86` and `:575`; a
  cancelled emit returns a compile-timeout message.
- [ ] Tests: oversize fragment rejected before compile (assert fast / Roslyn not invoked); in-bound
  fragment still compiles, caches, and its collectible ALC still unloads; timeout backstop under the
  opt-in benchmark.

## Phase 3 — Execution budget + K ceiling (R1, R4)
Intent: no traversal runs unbounded; hostile delegate bodies hit a hard deadline.
- [ ] Add a budget to `ShortestPathDefinition` (a `CancellationToken` + `int StepBudget`); flows into
  both algorithms with no `IShortestPathAlgorithm` signature change.
- [ ] `BLS.Calculate`: check the token / decrement the step budget at the top of the `do…while`
  (`BidirectionalLevelSynchronousSSSP.cs:171`) and per-frontier in `GetGlobalFrontier`; abort → distinct
  "budget exhausted" outcome.
- [ ] `Dijkstra.Search`: same at the `TryDequeue` loop (`WeightedDijkstraShortestPath.cs:245`) and the
  Yen outer loop (`:405`). Keep `effectiveMaxDepth` (`:144-149`) and the `MaxPathWeight` prune.
- [ ] `GraphController.CalculateShortestPath` (`:996`): build a `CancellationTokenSource(PathExecutionTimeout)`,
  set `StepBudget`, run the traversal on a worker task, abandon it on deadline (hard backstop for a
  delegate that ignores the token), map exhaustion/timeout → `408` (fallback `400`).
- [ ] Reject `MaxResults > MaxPathResults` at the controller → `400` before running.
- [ ] Tests: infinite-loop filter aborts within the budget and returns `408` (test owns a hard
  deadline); step budget bounds a large graph without a hostile delegate; pathological K-shortest is
  rejected (`400`) or aborted (`408`); a legitimate in-budget path is byte-identical to today.

## Phase 4 — REST surface & defaults
Intent: conservative defaults in the app; documented, tunable knobs; unlimited when unset.
- [ ] Configure `DynamicCodeLimits` in the API app with conservative defaults; map rejects (`400`) and
  budget exhaustion (`408`) with clear messages (mirror the `subgraph-quotas` controller mapping).
- [ ] Document each knob and the accepted allow-list type set; note limits are effectively unlimited
  when unset for embedded/trusted hosts.

## Measure & document
Intent: record the fix works and where the boundary is.
- [ ] Capture the opt-in compile-cost and abort-latency benchmark numbers (to be captured on this box).
- [ ] Note the residual (a hostile delegate that never returns leaks its worker thread) and cross-link
  `api-security-boundary` as the true-isolation follow-on; cross-link `codegen-cache-keying` (cache
  reuse) and `weighted-shortest-paths` (kept hop cap / weight prune).
- [ ] Full suite green.

## Progress
- [ ] Phase 0 — baseline & guardrail characterization tests + opt-in compile benchmark
- [ ] Phase 1 — type allow-list replaces every `Type.GetType(userString, …)` site
- [ ] Phase 2 — compile length caps + Roslyn parse/emit timeout
- [ ] Phase 3 — execution budget through BLS + Dijkstra/Yen, task-abandon deadline, `k` ceiling
- [ ] Phase 4 — REST defaults + status mapping + docs
- [ ] Measure & document

## Decision / revisit condition

This feature deliberately stops at **resource limits + a type allow-list** and does **not** attempt to
sandbox what a compiled filter may execute. In-process cooperative cancellation cannot interrupt user
code that ignores it, so a hostile delegate body is bounded only by the task-abandon deadline (which
leaks the runaway worker thread), not truly contained. That harder guarantee — a filter cannot observe
or affect process state, and a runaway is genuinely killable — is the out-of-process / WASM design in
`api-security-boundary` and is out of scope here.

**Revisit condition:** escalate to the `api-security-boundary` isolation design when Fallen-8 is
exposed to genuinely untrusted / multi-tenant callers, or when the leaked-thread residual from a
hostile delegate is observed to accumulate under load. Until then, the limits in this feature are the
sanctioned mitigation.
