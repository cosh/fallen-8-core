# OpenAPI 10.x — Plan

Companion to [spec.md](./spec.md). Vuln-safe bump → native XML → delete the workaround → verify the
doc still generates with content.

## Phase 1 — Vuln-safe package bump
- Bump `Microsoft.AspNetCore.OpenApi` → 10.x in `fallen-8-core-apiApp.csproj`; add an explicit pinned
  `Microsoft.OpenApi` reference at a patched version that clears NU1903. Run
  `dotnet list package --vulnerable --include-transitive` and confirm NO high-severity advisory
  (and no new NU1605 downgrade). Build clean.

## Phase 2 — Native XML + delete the transformer
- Enable the .NET 10 native OpenAPI XML-doc support (the package source generator; `GenerateDocumentationFile`
  already true — add any required opt-in). Confirm controller `<summary>`/`<remarks>`/`<response>` flow
  into the document.
- Delete `Helper/XmlDocumentationOperationTransformer.cs` and remove its registration in `Program.cs`
  (the operation-transformer wiring), keeping the rest of `AddOpenApi` + Scalar.

## Phase 3 — Verify the document
- Add/keep a test that builds the OpenAPI document via the framework document service (e.g. resolve
  `IOpenApiDocumentProvider`/the document service in a `WebApplicationFactory`, or the minimal
  document-generation path) and asserts: it generates without error, is OpenAPI 3.1.x, and contains a
  known operation's summary/description (proving the native XML reader works). If runtime generation
  genuinely can't be exercised in the harness, do the strongest build-time check available and state
  plainly what wasn't run.
- Full suite green; vuln scan clean.

## Phase 4 — Document
- Record the accepted output change (3.0.1 → 3.1.1, richer) here; update `dotnet10-modernization`
  spec/plan N3b to "landed (OpenAPI output change accepted; transformer removed)".

## Status
- [ ] Phase 1 — vuln-safe 10.x bump (+ pinned Microsoft.OpenApi)
- [ ] Phase 2 — native XML + delete transformer/wiring
- [ ] Phase 3 — verify the document generates with XML content
- [ ] Phase 4 — document the accepted change

## Notes
- No API surface / route / version change. Scalar stays.
- If the pinned `Microsoft.OpenApi` cannot clear the vuln, STOP and report (do not ship a
  high-severity advisory) — that would flip the decision back to keeping the transformer.
