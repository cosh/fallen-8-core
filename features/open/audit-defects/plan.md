# Audit defects: implementation plan

Six phases, ordered so that the changes with the widest blast radius land first and the OpenAPI snapshot is
regenerated exactly once, at the end. Each phase ends green: `dotnet build` at zero warnings and the full suite
passing.

Phases are grouped by the files they touch, not by severity, so that two fixes never contend for the same file.

## Phase 1: engine correctness (B10, B18, B25, B26)

The four defects that produce wrong answers.

- **B10** `VertexModel.GetAllNeighbors` projects in-edges through `TargetVertex`, which for an incoming edge is
  the vertex itself. Project in-edges through `SourceVertex`. Test: a vertex with distinct in- and
  out-neighbours returns both sets, and no self-entry appears unless a real self-loop exists.
- **B18** the REST DTO base converts the modification *delta* as if it were absolute. Add the `creationDate`
  term at the single conversion site. Test: an element created at a known stamp and then modified reports a
  `modificationDate` at or after its `creationDate`, and a never-modified element reports its creation stamp.
- **B25** level-0 seeding in the breadth-first subgraph algorithm matches edges without consulting
  `pattern.EdgeProperty`, unlike every deeper level. Apply the same filter at level 0. Test: a leading Edge
  pattern with an `edgePropertyFilter` excludes the edges it names, which today it silently keeps.
- **B26** recalculating a parent never re-resolves a nested child's source by `SourceFallen8Id`, so children
  point at the discarded instance. Re-resolve on recalculation. Test: recalculate a parent that has a nested
  child, then assert the child still resolves and reports the parent's new content.

B26 sits next to a leak the verifier found in the same code (a subgraph's engine is never disposed on
recalculation). That lead is not in this batch; note it in the report rather than fixing it opportunistically,
because disposing an engine that a child may still reference needs its own reasoning.

## Phase 2: guards and atomicity (B14, B19, B31, B39, B43)

Where the code lets something through that it documents as refused.

- **B14** refuse the generic index-content removals on a vector index bound to an embedding name, matching the
  existing refusal on adds. Test: the removal answers 400 and the projection still matches element state.
- **B19** route the create paths through the same guarded type conversion the scan and single-property routes
  use, so an unknown or unconvertible type is a 400. Test: both cases on `PUT /vertex` and `PUT /edge`.
- **B31** validate `pluginRegistration` before committing the rename, so `PATCH /ns/{name}` is all-or-nothing.
  Test: a valid name plus a bogus override answers 400 and leaves the old name addressable.
- **B39** check the path exists before registering it, so `PUT /load` answers the documented 400 instead of
  registering a phantom newest entry that aborts the next startup. Test: loading a missing path answers 400 and
  the registry is unchanged.
- **B43** take the `ServiceFactory` write lock in `DELETE /service/{key}` and stop the service before dropping
  it. Test: deletion is observable under concurrent reads and the service is stopped.

## Phase 3: plugin reach (B04, B28, B49)

Three halves of one story: a plugin the server accepts but can never run.

- **B04** `AnalyticsController.AlgorithmExists` consults only the base-directory DLL scan, so a registered
  Analytics plugin answers 404. Resolve against the registry as well.
- **B28** `PUT /subgraph` has no algorithm selector, so a registered `ISubGraphAlgorithm` is unreachable. Add
  the selector to `SubGraphSpecification` and thread it to the transaction, defaulting to the built-in.
- **B49** the MCP side: `f8_paths` advertises a closed `BLS`/`DIJKSTRA` enum and `f8_subgraph` takes no
  algorithm argument. Open both to a free-form name, matching REST, per the engine to REST to MCP rule.

This phase changes the request contract, so it is where the snapshot starts to move. Tests: a registered plugin
of each contract is invocable end to end, and an unknown name still fails cleanly.

## Phase 4: contract truth (B05, B06, B07, B16, B17, B29, B34, B42, B52)

Published samples and statuses that cannot be produced by the code that publishes them.

- **B05, B06, B42** correct the request samples so a copy-paste deserializes: properties as an array,
  `operator` on the wire as it really travels, and a `pluginType` that can actually resolve.
- **B07** a second `<remarks>` element is silently dropped, so the SECURITY and SEMANTIC notes never reach the
  document. Merge them into one block on each of the three code endpoints.
- **B16** remove the declared statuses the async pipeline cannot return, and document where those ceilings are
  actually enforced.
- **B17** correct the entity-type examples to the OntoNotes labels the shipped models emit.
- **B29** drop the stale `[DefaultValue(100.0)]` on `maxPathWeight` so generated clients stop pruning.
- **B34** set the document's title and version instead of inheriting the assembly name and `1.0.0`.
- **B52** stop leaking the R-Tree's `-1` "count not supported" sentinel as a real key count; report it the way
  `/status` already does.

## Phase 5: codegen, Studio, and the dev loop (B09, B12, B24, B53)

- **B09** apply the same fragment and generated-source length caps on the subgraph path that `/path` already has.
- **B24** short-circuit when a request supplies no filter and no cost, so no Roslyn run and no collectible
  assembly for a request with nothing to compile. Test: the codegen cache and assembly count are untouched.
- **B12** stop the Path screen offering an inline vertex filter alongside `costBySimilarity`, which the server
  rejects with 400.
- **B53** extend the Vite dev proxy allowlist to the prefixes the Studio actually calls, `/ns` first. A missing
  prefix returns the SPA shell instead of JSON, which is invisible to the client.

## Phase 6: cosmetic, then the gates (B01, B08, B13, B20, B33, B44, B45, B51, B55, B56)

Comments, messages, log lines, launch profiles, and the two environment scripts. No tests: these are text.

Then, once only:

1. Regenerate the OpenAPI snapshot (`powershell -File scripts/update-openapi-snapshot.ps1`; the script header
   says `powershell`, and `pwsh` is not installed on this box) and review the printed diff. Expect the phase 3
   selector additions, the phase 4 sample and metadata corrections, and nothing else.
2. Run the MCP coverage and contract gates.
3. Correct the two docs sentences that currently document a defect as expected behaviour (the offline pre-seed
   on the running page, the bound-index removals on the indexes page), and the plugin-registration and MCP
   pages' "known gaps" paragraphs that phase 3 makes obsolete.
4. Update `report.md`'s status column and move this directory to `features/done/`.
