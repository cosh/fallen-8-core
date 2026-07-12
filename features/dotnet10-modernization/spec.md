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

## 3. Important caveats

- **N3b vuln trade-off (first-hand from this session):** bumping `Microsoft.AspNetCore.OpenApi`
  to 10.0.x previously pulled `Microsoft.OpenApi 2.0.0`, which carries a **high-severity advisory
  (NU1903)**. Pin a patched `Microsoft.OpenApi` (verify a 2.x patch resolves NU1903) and confirm
  OpenAPI output parity **before** deleting the transformer. If the vuln can't be cleanly resolved,
  keep the transformer and defer N3b.
- **N1 scope:** the binary serializer's genuinely-polymorphic `object` fallback
  (`SerializationWriter.cs:606` / `SerializationReader.cs:1582`) must **stay** reflection-based —
  do not fold it into the source-gen context. Treat source-gen as a throughput/safety win and an
  AOT/trim *prerequisite*, not a one-step trim enabler. `GetGraphElement` returns the abstract
  `AGraphElement`, so declare `[JsonDerivedType]` polymorphism.
- **N5 DATAS:** no single right answer — benchmark DATAS on (helps memory-capped containers) vs off
  (`System.GC.DynamicAdaptationMode=0`, classic N-heaps/core throughput on big hosts) against a
  representative graph. Add Server/Concurrent GC props to the **library** csproj too (a non-web host
  would otherwise get Workstation GC).

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
  reduction on index/service open paths.
- N3: `PerformanceCounter` gone; either OpenAPI 10.x + transformer deleted (with the vuln resolved)
  or a documented decision to defer; `Microsoft.Extensions.*` aligned; build clean.
- N4: save/load string throughput improves; no on-disk format change (bytes identical).
- N5: `docker build` produces a runnable net10 image; GC settings explicit; a DATAS benchmark
  recorded to justify the chosen mode.
