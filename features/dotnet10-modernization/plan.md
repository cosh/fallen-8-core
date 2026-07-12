# .NET 10 Modernization — Plan

Companion to [spec.md](./spec.md). Items N1–N5 defined there. Ordered small-high-signal first.

## Phase 1 — Cleanup & tooling (S)
- **N3a** remove `System.Diagnostics.PerformanceCounter` (verify no usage; `AdminController` memory
  stat uses `System.Diagnostics.Process`).
- **N4** serializer bulk byte I/O (single `Write(byte[])` / `ReadBytes`), encoding unchanged.
- **N5 (build)** update the Dockerfile to .NET 10 base+SDK images; verify `docker build` runs net10.

## Phase 2 — Package alignment (S–M)
- **N3c** align `Microsoft.Extensions.*` to 10.x.
- **N3b** attempt OpenAPI 10.x: first resolve the `Microsoft.OpenApi` NU1903 advisory (pin a patched
  2.x); confirm OpenAPI output parity with the current transformer; only then delete
  `XmlDocumentationOperationTransformer` and simplify `Program.cs`. If the vuln can't be resolved,
  keep the transformer and record the deferral.

## Phase 3 — JSON source generation (M)
- **N1** add an `AppJsonContext : JsonSerializerContext` covering the REST DTOs, `SubGraphRecipe`,
  `SubGraphSpecification`, and `DelegateDescriptor`; wire into MVC JSON options and the explicit
  `JsonSerializer` call sites; declare `[JsonDerivedType]` for `AGraphElement`. Leave the binary
  serializer's `object` fallback reflection-based. Remove the now-unneeded IL2026 suppressions.

## Phase 4 — GC configuration & DATAS (S–M)
- **N5 (GC)** add explicit `<ServerGarbageCollection>`/`<ConcurrentGarbageCollection>` to both the
  app and the **library** csproj; benchmark DATAS on vs off against a representative resident graph;
  record the decision.

## Status
- [x] Phase 1 — remove PerformanceCounter (N3a), serializer bulk I/O (N4), Dockerfile → net10
  (N5 build; `docker build` not executed in-environment)
- [~] Phase 2 — `Microsoft.Extensions.*` aligned to 10.0.9 (N3c, done). OpenAPI 10.x (N3b)
  **deferred**: the `Microsoft.OpenApi` NU1903 advisory is resolvable (pin `Microsoft.OpenApi`
  2.10.0), but 10.x native XML-doc reading changes the OpenAPI document (3.0.1 → 3.1.1, added
  descriptions/examples), which breaks the "output unchanged" contract. Transformer + `Program.cs`
  wiring kept; vulnerability not reintroduced. See spec.md §3.
- [x] Phase 3 — JSON source generation (N1): `AppJsonContext` (app DTOs + `SubGraphSpecification`)
  wired into MVC + the explicit call sites; `CoreJsonContext` (library) for `SubGraphRecipe` +
  `DelegateDescriptor`. JSON and OpenAPI output proven byte-identical; JSON-specific IL2026
  suppressions removed.
- [x] Phase 4 — explicit Server/Concurrent GC props added to app + library csproj (N5). DATAS left
  at the .NET 10 default; no benchmark executed (documented in spec.md §3).
