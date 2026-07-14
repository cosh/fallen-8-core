# Dynamic-Code Resource Limits & Type Allow-List — Specification

> **Status:** Partially implemented (P1 security / DoS) — from the 2026-07 principal-architect &
> performance review. Compiled user filter/cost fragments ran with no compile bound and no execution
> budget, and attacker-controlled type names were resolved via `Type.GetType`.
>
> **Delivered on branch `feature/dynamic-code-resource-limits`:**
> - **R3 — type allow-list.** `AllowedLiteralTypes` is a closed, case-insensitive allow-list of the
>   primitive literal types (keyed by full name, short name, and C# aliases). Every
>   `Type.GetType(userString, throwOnError: true)` site — the `GraphController` scan/property paths and
>   the four `ServiceHelper` sites — now resolves through it and **never** calls `Type.GetType`, so an
>   attacker-controlled name cannot force-load an assembly or run a static ctor (reachable even on read
>   endpoints). A disallowed name on the scan/property controllers is a `400`.
> - **R2 — compile length cap.** `CodeGenerationHelper` rejects any filter/cost fragment longer than
>   `MaxFilterFragmentLength` (100 000) or a generated source longer than `MaxGeneratedSourceLength`
>   (1 000 000) **before** Roslyn is invoked, returning the human-readable message the controllers map
>   to a `400`. The length cap is the load-bearing compile guard (Roslyn cancellation is only
>   cooperative).
>
> Guarded by `DynamicCodeResourceLimitsTest` (allow-list resolves primitives / rejects
> `System.Console`/`System.IO.File`/arbitrary names without `Type.GetType`; disallowed scan type → 400;
> allowed primitive still works; oversize fragment → 400).
>
> **Deferred (documented):**
> - **R1 — execution budget + task-abandon backstop.** Threading a `CancellationToken`/step budget
>   through both path algorithms plus a `Task.Run`+bounded-wait deadline in the controller is the most
>   invasive part, and — as §5 states — cooperative cancellation cannot truly interrupt a hostile
>   delegate that never returns (the backstop leaks the runaway thread). Real isolation is the
>   out-of-process / WASM design tracked in `api-security-boundary`; this budget is a follow-on to pair
>   with it, not a substitute.
> - **R4 — `MaxResults`/K ceiling.** `MaxResults` is a `UInt16` already bounded to 65 535 and defaults
>   to 65 535, so a policy ceiling would reject the default and needs a config knob plus a lower default
>   to be useful; the genuine K×lambda bound belongs to the deferred R1 budget.
> - The `CompileTimeout`/`PathExecutionTimeout` config-object plumbing (`DynamicCodeLimits`) is deferred
>   with R1; the landed R2/R3 use generous static defaults.

## 1. Problem / current state

The path and subgraph REST APIs accept C# code fragments as strings (e.g.
`"return (v) => v.Label == \"person\";"`), compile them with Roslyn into `Delegates.*` types, and
run the compiled delegates in the traversal hot loop. Several unrelated scan/property endpoints
resolve a caller-supplied `FullQualifiedTypeName` with `Type.GetType(userString, true, true)`. None
of this input is bounded, so an authenticated caller can hang a worker thread indefinitely, burn
arbitrary compile-time CPU/memory, or trigger a second code/side-effect surface via type resolution.

| # | Issue | Location (verified) | Effect |
|---|-------|---------------------|--------|
| R1 | **No execution budget / cancellation.** The compiled filter/cost delegates run inside the traversal loops with no `CancellationToken` or deadline. A filter body of `return (v) => { while(true){} };` never returns, hanging the request thread forever. | `BidirectionalLevelSynchronousSSSP.cs:171-226` (the `do … while(true)` frontier loop; delegates invoked in `GetGlobalFrontier`); `WeightedDijkstraShortestPath.cs:245` (the `TryDequeue` loop) and `:405` (the Yen K loop). Neither `IShortestPathAlgorithm.TryCalculateShortestPath` (`IShortestPathAlgorithm.cs:47`) nor `ShortestPathDefinition` (`ShortestPathDefinition.cs`) carries a token/deadline. Entry point `GraphController.CalculateShortestPath` (`GraphController.cs:996`) runs the traversal synchronously on the request thread. | Remote DoS: one request permanently consumes a thread; repeated requests exhaust the pool. |
| R2 | **No compile bound.** `compilation.Emit` runs with no timeout and no cap on fragment or generated-source length, so a large/pathological fragment burns arbitrary CPU/memory at compile time. | `CodeGenerationHelper.cs:86` (`GeneratePathTraverser` → `Emit`) and `:575` (`CompileProvider` → `Emit`); no length check in `GenerateMethodSyntax` (`:187`) or `BuildProviderSource` (`:606`). | Remote DoS at compile time, before any traversal runs. |
| R3 | **Arbitrary type resolution.** `Type.GetType(userString, true, true)` on an attacker-controlled name, then `Convert.ChangeType`. Resolving an arbitrary type runs its static constructor and can force-load an assembly — reachable even on **read** endpoints. | `GraphController.cs:503` (`GraphScan`, read), `:552` (`IndexScan`, read), `:598`/`:601` (`RangeIndexScan`, read), `:738` (`AddProperty`, mutation). Also `ServiceHelper.cs:73` (`CreateObject`), `:95`/`:96` and `:123`/`:124` (both `GenerateProperties` overloads), `:138` (`Transform`). | Code/side-effect surface: static-ctor execution + assembly force-load driven by untrusted strings on read paths. |
| R4 | **K-shortest compute not fully bounded.** `MaxResults` (`k`) has no ceiling; each of the `k` Yen iterations runs a spur Dijkstra search that invokes the (possibly hostile) cost lambda. | `WeightedDijkstraShortestPath.cs:405` (Yen loop, `for index = 1 … k`); `MaxResults` flows unbounded from `ShortestPathDefinition.MaxResults` (`:64`). | A large `k` with an expensive cost lambda multiplies the per-call work. |

**Correction vs. the review brief (verified in the current tree):** the brief framed R4 as "no compute
cap beyond `MaxDepth`". The weighted algorithm already caps the hop dimension at
`effectiveMaxDepth = min(MaxDepth, VertexCount-1)` (`WeightedDijkstraShortestPath.cs:144-149`) and
prunes on `MaxPathWeight` (both landed in `weighted-shortest-paths`). So the genuinely-remaining gap
is (a) no time/step budget through the loops and (b) `k`/`MaxResults` is itself unbounded — **not** a
missing hop cap. R4 is scoped to those two, and the existing hop cap + weight prune must be kept.

Note also `CodeGenerationHelper.cs:259` already bounds one untrusted-driven combinatorial surface:
`MaxVariableEdgeLength = 100` caps a subgraph variable-length edge pattern. It is the precedent this
work extends to the compile and execution budgets — keep it.

## 2. Goals / non-goals

**Goals**
- A per-fragment and per-generated-source **length cap** enforced *before* Roslyn is invoked, plus a
  **compile timeout** threaded into `ParseSyntaxTree`/`Emit` (R2).
- An **execution budget** (wall-clock deadline **and** a step/branch budget) threaded through the
  path traversal loops so both an algorithmic blow-up and a hostile delegate are bounded; exhaustion
  aborts and maps to a clear 4xx (R1, R4).
- A **type allow-list** replacing every `Type.GetType(userString, …)` site: an accepted primitive
  resolves, anything else is rejected with `400` and **no** `Type.GetType` call / assembly load /
  static-ctor execution (R3).
- A ceiling on `MaxResults` (`k`) for the weighted K-shortest path (R4).
- All limits **configurable with safe defaults**, effectively-unlimited/large when unset for
  trusted/embedded use — mirroring the `SubGraphQuota` shape from `subgraph-quotas`.

**Non-goals**
- **True sandboxing of what a filter may do** — restricting the code a compiled delegate can execute,
  the state it can observe, or the side effects it can cause. In-process, cooperative cancellation
  cannot interrupt user code that ignores it (see §5); real isolation is the out-of-process / WASM
  design tracked in `api-security-boundary`. Referenced, not attempted here.
- Authentication / per-caller quotas (also `api-security-boundary`).
- Changing the transaction/single-writer model or the lock-free read model; the path endpoints are
  reads and stay reads.
- Fixing the per-request traverser cache miss (that is `codegen-cache-keying` / `engine-performance`
  P1) — this feature must not regress caching but does not own it.

## 3. Design sketch

A `DynamicCodeLimits` options object (mirroring `SubGraphQuota` from `subgraph-quotas`), surfaced on
the host and defaulted conservatively by the REST app, effectively unlimited when unset:

- `MaxFilterFragmentLength`, `MaxGeneratedSourceLength` (chars)
- `CompileTimeout` (a `TimeSpan`)
- `PathExecutionTimeout` (a `TimeSpan`), `PathStepBudget` (max frontier expansions / dequeues)
- `MaxPathResults` (ceiling on `k`)

**(A) Compile bounds — `CodeGenerationHelper`.** Before building the syntax tree, reject any single
filter/cost fragment longer than `MaxFilterFragmentLength` and the assembled source longer than
`MaxGeneratedSourceLength`, returning the existing human-readable error string (the controllers
already map a non-null compiler message to a failed compile). Pass a
`new CancellationTokenSource(CompileTimeout).Token` into `SyntaxFactory.ParseSyntaxTree(…, ct)` and
`compilation.Emit(ms, cancellationToken: ct)` (both Roslyn calls accept a token) — on cancellation,
return a compile-timeout message. The length cap is the primary guard (Roslyn cancellation is
cooperative); the timeout is a backstop. Applies symmetrically to `GeneratePathTraverser`/`CreateSource`
and the subgraph `CompileProvider`/`BuildProviderSource` path.

**(B) Execution budget — `ShortestPathDefinition` + both algorithms.** Add a budget to
`ShortestPathDefinition` (a `CancellationToken` plus an `int StepBudget`) so it flows into both
algorithms without changing the `IShortestPathAlgorithm` signature. The controller builds a
`CancellationTokenSource(PathExecutionTimeout)`, sets `StepBudget = PathStepBudget`, and threads them
in. In `BLS.Calculate` check the token / decrement the step counter at the top of the `do…while`
and per-frontier in `GetGlobalFrontier`; in `Dijkstra.Search` check at the top of the `TryDequeue`
loop and in the Yen outer loop. On exhaustion, abort cleanly (return "no result" plus a distinct
signal) and have the controller map it to **408 Request Timeout** (fall back to 400).

Because a compiled delegate can itself `while(true)` and a token check *between* delegate calls
cannot interrupt a single call that never returns, the deadline needs a hard backstop: the controller
runs the traversal on a worker task and abandons it if `PathExecutionTimeout` elapses
(`Task.Run(…)` + bounded wait), returning 408. The cooperative token/step checks give a fast, clean
abort for algorithmic blow-up (large graph, huge `k`); the task-abandon deadline covers a hostile
delegate body. The abandoned worker thread is lost until its delegate returns (it may not) — this is
the residual documented in §5 and the reason true isolation is `api-security-boundary`.

**(C) Type allow-list.** Add `AllowedLiteralTypes.TryResolve(string name, out Type type)` backed by a
static, case-insensitive map of the primitives literals legitimately use — `String`, `Boolean`,
`Byte`, `SByte`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Single`, `Double`,
`Decimal`, `Char`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid` (keyed by full name and common
short aliases). Replace **every** `Type.GetType(userString, …)` site (the four `GraphController`
sites and the four `ServiceHelper` sites in R3) with a `TryResolve` lookup: hit → `Convert.ChangeType`
as today; miss → reject with `400` and a clear message, never calling `Type.GetType`, so no assembly
loads and no static ctor runs. `Convert.ChangeType` is then only ever handed a vetted primitive type.

**(D) K ceiling.** Reject `MaxResults > MaxPathResults` at the controller with `400` before running
the traversal. Keep `effectiveMaxDepth` and the `MaxPathWeight` prune (`weighted-shortest-paths`)
untouched; the §3(B) budget bounds the Yen loop's total compute, and the ceiling makes the worst-case
number of spur searches explicit.

Status mapping mirrors the `subgraph-quotas` "bounded, 4xx" pattern: oversize/allow-list/`k`-ceiling
rejects → `400`; execution-budget exhaustion / timeout → `408` (fallback `400`).

## 4. Acceptance criteria

- An infinite-loop filter (`return (v) => { while(true){} };`) on `/path` **aborts within the
  configured budget** and returns `408`, instead of hanging the thread forever (pinned by a test with
  its own hard wall-clock deadline so the suite itself never hangs).
- An oversize fragment / generated source is **rejected before Roslyn is invoked** (fast, no compile),
  returning `400`; the compile-timeout backstop is exercised by an opt-in benchmark.
- A non-allow-listed `FullQualifiedTypeName` (e.g. a real type with an observable static-ctor side
  effect) returns `400` and provably **does not load its assembly / run its static ctor**; every
  accepted primitive still round-trips through the scan/property endpoints unchanged.
- A pathological K-shortest request (large `MaxResults`, expensive cost lambda) is **bounded**: either
  rejected by the `MaxPathResults` ceiling (`400`) or aborted by the execution budget (`408`).
- Legitimate in-budget path/subgraph/scan requests return **byte-identical** results to today.
- Limits unset ⇒ behaviour matches today; the full existing suite stays green.

## 5. Risks

- **Cooperative cancellation cannot interrupt a hostile delegate that never returns.** The
  task-abandon backstop returns 408 but leaks the runaway worker thread until (if ever) the delegate
  returns. Bounded by request rate; the real fix is out-of-process / WASM isolation in
  `api-security-boundary`. This must be stated honestly in the docs, not hidden.
- **Too-tight defaults abort legitimate large traversals.** Defaults must be generous and configurable;
  the step budget should be large enough for real graphs and only bite on abuse.
- **Allow-list completeness.** It must cover every type today's callers legitimately send
  (`System.String`, `System.Int32`, …); an exotic type a caller relies on would stop resolving. Given
  the code-exec/DoS surface this is an acceptable, documented trade-off — publish the accepted set.
- **Roslyn `Emit`/`Parse` cancellation is cooperative**, so the length cap (not the timeout) is the
  load-bearing compile guard.

## 6. Keep (do not regress)

- **Collectible `AssemblyLoadContext` per compiled filter** (`collectible-codegen-assemblies`, landed):
  the new bounds run before/around compilation and must not break assembly unload.
- **Content-keyed compiled-provider cache** (`_subGraphProviderCache`, `CodeGenerationHelper.cs:267`)
  and the per-request traverser cache: length/timeout checks run before caching a compile result and
  must not defeat cache reuse for identical, in-bound fragments.
- **`MaxVariableEdgeLength = 100`** (`CodeGenerationHelper.cs:259`) — the existing variable-length
  pattern guard; the new limits complement it.
- **`effectiveMaxDepth` hop cap + `MaxPathWeight` prune** (`weighted-shortest-paths`,
  `WeightedDijkstraShortestPath.cs:144-149`, `:287`) — keep; R4 adds a `k` ceiling, not a hop change.
- **Single-writer / lock-free reads**; path endpoints stay reads. Correct results for in-budget
  requests are unchanged.
