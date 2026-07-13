# OpenAPI 10.x — Specification

> **Status:** Landed. The `dotnet10-modernization` **N3b** item, deferred there because the 10.x
> native XML-doc path *changes the emitted OpenAPI document* (3.0.1 → 3.1.1, richer descriptions +
> examples) and that theme's contract was "output unchanged". **User decision (this feature): do the
> bump and ACCEPT the richer 3.1 document as an improvement.**
>
> **Outcome — landed.** `Microsoft.AspNetCore.OpenApi` 9.0.4 → **10.0.9** with an explicit
> `Microsoft.OpenApi` **2.10.0** pin; the vulnerable-package scan reports **no vulnerable packages**
> (NU1903 not reintroduced), no NU1605 downgrade, build 0/0. The
> `XmlDocumentationOperationTransformer` and its `Program.cs` wiring are gone; controller XML docs
> now flow in through .NET 10's native reader. The emitted document is **OpenAPI 3.1.1** (was 3.0.1),
> with `"description"` up 119 → **324** and `"example"` 0 → **111** while the `$ref` count is
> unchanged (122 → 122): same API surface (identical 44 paths / 49 operations), richer documentation.
> The 10.x path also fixes an invalid-JSON bug on non-invariant locales (older Microsoft.OpenApi
> serialized the edge-weight `default` culture-sensitively — e.g. `1,5` on a German-locale host; 10.x
> emits `1.5`). Verified at runtime by
> `OpenApiDocumentTest` (boots the app via `WebApplicationFactory<Program>` and GETs
> `/openapi/v0.1.json`). Full suite green (344 passed / 10 skipped).

## 1. Problem / current state

`Microsoft.AspNetCore.OpenApi` is pinned at **9.0.4**. To surface XML `<summary>`/`<remarks>`/
`<response>` in the OpenAPI doc, the app carries a hand-written `Helper/XmlDocumentationOperationTransformer`
wired in `Program.cs` — a workaround for 9.x not reading XML comments natively. .NET 10's
`Microsoft.AspNetCore.OpenApi` reads XML doc comments **natively** (build-time source generator over
the `GenerateDocumentationFile` output), making the transformer redundant and producing a more
complete document. The bump was deferred to avoid (a) the `Microsoft.OpenApi` **NU1903** high-severity
advisory a naive 10.x bump pulled in, and (b) the emitted-document change. The user has accepted (b);
(a) is resolved by pinning a patched `Microsoft.OpenApi`.

## 2. Design

- Bump `Microsoft.AspNetCore.OpenApi` 9.0.4 → **10.x** and pin `Microsoft.OpenApi` to a **patched**
  version (the dotnet10 review identified 2.10.0 as clearing NU1903 — verify the actual resolved
  version has NO high-severity advisory).
- Enable the .NET 10 **native XML documentation** support for OpenAPI (the package's source generator;
  `GenerateDocumentationFile` is already `true`) so the controller XML comments flow into the document
  without a custom transformer.
- **Delete `Helper/XmlDocumentationOperationTransformer.cs`** and its `Program.cs` wiring (the
  `.AddOpenApi(o => o.AddOperationTransformer(...))` / equivalent). Keep the rest of the OpenAPI +
  Scalar setup.
- Accept the document version moving to **OpenAPI 3.1.1** and the richer output (more descriptions /
  examples). This is an enrichment, not a regression.
- The `dotnet10-modernization` N1 JSON source-gen (`AppJsonContext`) and everything else stay as-is.

## 3. Acceptance criteria

- `Microsoft.AspNetCore.OpenApi` is on 10.x; `dotnet list package --vulnerable --include-transitive`
  reports **NO high-severity advisory** (NU1903 not reintroduced).
- The `XmlDocumentationOperationTransformer` class and its wiring are gone; build is clean (0/0), no
  new analyzer/trim warnings.
- The OpenAPI document still **generates validly** and still carries the XML-doc content (operation
  summaries/remarks/response descriptions) via the native reader — verified by a test that builds the
  document through the framework's OpenAPI document service (or, if runtime generation can't be
  exercised in the harness, a clearly-stated build-time verification + manual note).
- Full suite green. The accepted output change (3.0.1 → 3.1.1, richer) is documented here and the
  dotnet10 N3b note updated to "landed (output change accepted)".

## 4. Non-goals

- Changing the API surface, routes, versioning (`api/v0.1`), or the Scalar UI wiring.
- Preserving byte-identical OpenAPI output (explicitly NOT a goal — the richer 3.1 doc is accepted).
