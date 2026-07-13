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
- [x] Phase 1 — vuln-safe 10.x bump. `Microsoft.AspNetCore.OpenApi` 9.0.4 → **10.0.9**; explicit
  `Microsoft.OpenApi` **2.10.0** pin. `dotnet list package --vulnerable --include-transitive`
  reports **no vulnerable packages** for all three projects (queried against
  `https://api.nuget.org/v3/index.json`); no NU1605 downgrade; build 0 warnings / 0 errors.
- [x] Phase 2 — native XML + delete transformer/wiring. `AddOpenApi("v0.1")` now has no operation
  transformer; `Helper/XmlDocumentationOperationTransformer.cs` deleted. Native XML reading needs no
  extra opt-in beyond the package reference + `GenerateDocumentationFile` (already `true`): the
  package's `Microsoft.AspNetCore.OpenApi.targets` auto-registers the interceptor namespace and
  feeds project-reference `.xml` files to its source generator. The rest of `AddOpenApi` + the
  Scalar UI wiring is untouched.
- [x] Phase 3 — verify the document generates with XML content. `OpenApiDocumentTest` boots the real
  app via `WebApplicationFactory<Program>` (Development env, so the endpoint is mapped), GETs
  `/openapi/v0.1.json`, and asserts **200 OK**, `openapi` starts with **`3.1`**, a known operation
  **summary** ("Creates a new vertex in the graph") and a **description** ("Sample request") sourced
  from controller `<summary>`/`<remarks>`. This runs the genuine runtime doc-generation +
  serialization path, not a build-time proxy. Full suite: **344 passed / 10 skipped / 0 failed**.
- [x] Phase 4 — document the accepted change (this file + spec.md; dotnet10 N3b flipped to landed).

## Observed output (10.0.9 + Microsoft.OpenApi 2.10.0, SDK 10.0.201)
- OpenAPI version **3.1.1** (was 3.0.1 under the 9.x transformer).
- `"description"` occurrences **324** (transformer-era baseline: 118); `"example"` **111**
  (baseline: 0); `"$ref"` **122** (baseline ~119 — API surface unchanged, documentation richer).

## Notes
- No API surface / route / version change. Scalar stays.
- If the pinned `Microsoft.OpenApi` cannot clear the vuln, STOP and report (do not ship a
  high-severity advisory) — that would flip the decision back to keeping the transformer. It cleared
  cleanly, so this did not apply.
