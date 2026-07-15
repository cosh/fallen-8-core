# .NET 10 Modernization — Specification

> **Status:** Planned. Framework/tooling adoption from the review, specific to this codebase (not
> a generic .NET 10 checklist). The runtime defaults are already good — this is targeted wins and
> cleanup.

## 1. Verified current state

- `TargetFramework net10.0` (all three projects); `LangVersion` unset → C# 14 (collection
  expressions already used, e.g. `Fallen8.cs:173`).
- Server GC effectively **on** (Web SDK default; `runtimeconfig.json`); Concurrent GC / Tiered /
  PGO / DATAS on by runtime default. `PublishTrimmed=false` (correct given reflection + runtime
  codegen).
- Packages on a net10 target: `Microsoft.AspNetCore.OpenApi` **9.0.4**,
  `System.Diagnostics.PerformanceCounter` **9.0.4** (unused), `Microsoft.Extensions.Caching.Memory`/
  `Logging` **9.0.4**.
- **`Dockerfile` still targets .NET 3.1** — cannot build/run net10.
- Concurrency uses `Interlocked` spinlocks + `ConcurrentDictionary`, **no monitor `lock`s** in the
  engine (only a benchmark helper).

## 2. Recommendations (ranked)

| # | Item | Where | Benefit | Effort |
|---|------|-------|---------|--------|
| N1 | System.Text.Json **source generation** (`JsonSerializerContext`) for REST DTOs + recipe/spec serialization | `Controllers/Model/*` (31 DTOs); `PersistencyFactory.cs:290,319`; `RecipeSubGraphCompiler.cs:67`; `SubGraphController.cs:164` | throughput + faster startup; removes the reflection forcing the current IL2026 suppressions | M |
| N2 | Memoize plugin discovery + `FrozenDictionary<string,Type>` name→type map | `Plugin/PluginFactory.cs:176` (re-scans + `Assembly.Load`s every DLL on every index/service/save/load op) | eliminates repeated disk scan + reflection; fastest steady-state lookup | M |
| N3a | Remove unused `System.Diagnostics.PerformanceCounter` package (Windows-only; zero usages; app deploys Linux) | `fallen-8-core-apiApp.csproj:38` | deletes a native dependency | S |
| N3b | Bump `Microsoft.AspNetCore.OpenApi` to 10.x (native XML-doc reading) and **delete `XmlDocumentationOperationTransformer`** | `fallen-8-core-apiApp.csproj:34`; `Helper/XmlDocumentationOperationTransformer.cs`; `Program.cs:62` | removes a whole workaround class | S–M |
| N3c | Align `Microsoft.Extensions.*` to 10.x | `fallen-8-core.csproj:40` | avoid mixed 9↔10 resolution | S |
| N4 | Serializer **bulk byte I/O** (replace per-byte string loops; keep encoding unless versioned) | `SerializationWriter.cs:342`; `SerializationReader.cs:213` | save/load speedup, no format change | S |
| N5 | Fix the **Dockerfile** to .NET 10 base+SDK images; make GC config explicit; evaluate DATAS on/off for a resident graph | `Dockerfile:3,8`; `.csproj` GC props | buildable container; GC predictability | S–M |

> **N2 scope note:** N2 (memoize plugin discovery + `FrozenDictionary` name→type map) is a
> steady-state runtime performance optimization, not a framework/tooling modernization. It is
> **not** implemented under this feature (the plan's phases deliberately omit it) and belongs to the
> `engine-performance` theme. Tracked there rather than here.

## 3. Important caveats

- **N3b vuln trade-off (first-hand from this session):** bumping `Microsoft.AspNetCore.OpenApi`
  to 10.0.x previously pulled `Microsoft.OpenApi 2.0.0`, which carries a **high-severity advisory
  (NU1903)**. Pin a patched `Microsoft.OpenApi` (verify a 2.x patch resolves NU1903) and confirm
  OpenAPI output parity **before** deleting the transformer. If the vuln can't be cleanly resolved,
  keep the transformer and defer N3b.
  - **Outcome — LANDED (delivered under the `openapi-10` feature; output change accepted, transformer
    removed).** Verified on .NET SDK 10.0.201: bumping to
    `Microsoft.AspNetCore.OpenApi 10.0.9` pulls `Microsoft.OpenApi 2.0.0` and reproduces NU1903
    (`GHSA-v5pm-xwqc-g5wc`). The advisory **is** cleanly resolvable by pinning
    `Microsoft.OpenApi 2.10.0` (`dotnet list package --vulnerable --include-transitive` then reports
    no vulnerable packages, no NU1605 downgrade). However, the second gate — OpenAPI output parity —
    fails: the 9.x custom `XmlDocumentationOperationTransformer` does not compile against the
    reshaped `Microsoft.OpenApi` 2.x object model, so completing the bump requires deleting it and
    relying on 10.x native XML-doc reading. That native path materially changes the emitted
    document (OpenAPI version `3.0.1` → `3.1.1`; `"description"` occurrences 119 → 324;
    `"example"` occurrences 0 → 111; `$ref`s unchanged at 122, i.e. the API surface is identical
    (same 44 paths / 49 operations) but the documentation is richer). Under the original "OpenAPI
    output unchanged" contract this blocked
    N3b, so it was **deferred** here. The user then **accepted** the richer 3.1.1 document as an
    improvement, and N3b **landed** under the dedicated **`openapi-10`** feature:
    `Microsoft.AspNetCore.OpenApi` is on **10.0.9** with `Microsoft.OpenApi` **2.10.0** pinned, the
    transformer + `Program.cs` wiring are **removed**, .NET 10 native XML-doc reading is enabled, and
    a runtime `OpenApiDocumentTest` (via `WebApplicationFactory<Program>`) confirms the 3.1.1 document
    still carries the controller XML content. The vulnerability is **not** reintroduced. See
    `features/openapi-10/`.
- **N1 scope:** the binary serializer's genuinely-polymorphic `object` fallback
  (`SerializationWriter.cs:606` / `SerializationReader.cs:1582`) must **stay** reflection-based —
  do not fold it into the source-gen context. Treat source-gen as a throughput/safety win and an
  AOT/trim *prerequisite*, not a one-step trim enabler. `GetGraphElement` returns the abstract
  `AGraphElement`, so declare `[JsonDerivedType]` polymorphism.
  - **Outcome — `[JsonDerivedType]` intentionally NOT declared.** ASP.NET Core's output formatter
    serializes response bodies by **runtime** type, so `GetGraphElement` already emits the concrete
    `Vertex`/`Edge` shape with no `$type` discriminator. Registering the concrete `Vertex` and
    `Edge` (whose generated metadata includes the inherited `AGraphElement` properties) reproduces
    that output exactly. Adding `[JsonDerivedType]` would instead introduce a polymorphic
    `$type`/`oneOf` discriminator into both the JSON and the OpenAPI schema — a behaviour change —
    so it was omitted to honour "output unchanged". Two source-gen contexts are used because of the
    project split: `AppJsonContext` (app: REST DTOs + `SubGraphSpecification`) and `CoreJsonContext`
    (engine: `SubGraphRecipe` + the internal `DelegateDescriptor`); a single app-level context
    cannot serve the library call sites (`PersistencyFactory`, `DelegateJson`). Parity is proven
    byte-for-byte for both the JSON (unit test) and the OpenAPI document (before/after capture).
- **N5 DATAS:** no single right answer — benchmark DATAS on (helps memory-capped containers) vs off
  (`System.GC.DynamicAdaptationMode=0`, classic N-heaps/core throughput on big hosts) against a
  representative graph. Add Server/Concurrent GC props to the **library** csproj too (a non-web host
  would otherwise get Workstation GC).
  - **Outcome — DATAS left at the .NET 10 runtime default (on); no benchmark executed.** Explicit
    `<ServerGarbageCollection>true</ServerGarbageCollection>` and
    `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` were added to **both** the app
    and the library csproj. `DynamicAdaptationMode` is deliberately **not** set, so DATAS keeps its
    .NET 10 default (enabled). Trade-off: **DATAS on** lets the heap count adapt to the live data
    set and container memory limits — better default behaviour for memory-capped container
    deployments (the app's target); **DATAS off** (`System.GC.DynamicAdaptationMode=0`) pins the
    classic one-heap-per-core Server GC layout, which can give higher steady-state throughput on a
    large dedicated host with a big resident graph. No benchmark was run: this environment cannot
    host a representative resident graph under a realistic load, so any numbers would be fabricated.
    A real evaluation would load a representative graph, then compare a sustained
    query/mutation workload under `DOTNET_GCDynamicAdaptationMode=1` vs `=0` (and Server vs
    Workstation), recording end-to-end throughput, p99 latency, working set, and GC pause time
    (e.g. via `dotnet-counters`/`dotnet-trace`) before choosing to override the default.

## 4. Explicitly low/no value here (don't do)

- `System.Threading.Lock` — engine uses `Interlocked`/`ConcurrentDictionary`, not monitors. (The
  one benchmark `lock` is better replaced by `Interlocked.Add`.)
- `FrozenDictionary` beyond N2 — the graph indices are mutated at runtime (not frozen candidates).
- `SearchValues<T>` — only a single-delimiter whitespace scan exists; `Span.IndexOf(' ')` suffices.
- Broad language-polish campaigns — do opportunistically, not as a work item.

## 5. Acceptance criteria

- N1: DTO + recipe/spec serialization go through the generated context; the explicit-call-site
  IL2026 suppressions are removed; OpenAPI/serialization output unchanged.
- N2: plugin discovery runs the disk scan once (cache invalidated on `Assimilate`); benchmarked
  reduction on index/service open paths. *(out of scope for this theme — tracked as
  engine-performance P5; see §2 note)*
- N3: `PerformanceCounter` gone; OpenAPI on 10.x with the transformer deleted and the vuln resolved
  (landed under `openapi-10`; the earlier deferral is superseded); `Microsoft.Extensions.*` aligned;
  build clean.
- N4: save/load string throughput improves; no on-disk format change (bytes identical).
- N5: `docker build` produces a runnable net10 image; GC settings explicit; a DATAS benchmark
  recorded to justify the chosen mode *(deferred — no benchmark run; see §3 and the GC
  decision)*.
