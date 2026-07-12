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
- [ ] Phase 1 — remove PerformanceCounter, serializer bulk I/O, Dockerfile → net10
- [ ] Phase 2 — package alignment + OpenAPI 10.x (vuln-gated) + delete transformer
- [ ] Phase 3 — JSON source generation
- [ ] Phase 4 — explicit GC config + DATAS benchmark
