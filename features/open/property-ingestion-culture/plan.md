# Property ingestion/egress culture — Implementation Plan

Branch `feature/property-ingestion-culture` from `main`. Single phase; small, self-contained.

## Phase 1 — invariant round-trip + tests

1. **C1 ingest** — `Helper/ServiceHelper.cs` `CreateObject`: `Convert.ChangeType(value,
   type, CultureInfo.InvariantCulture)`; add `using System.Globalization;`. This is the
   one-home explanation for the ingest side.
2. **C2 egress** — `Controllers/Model/AGraphElement.cs` `FormatPropertyValue`: add
   `case IFormattable formattable: return formattable.ToString(null,
   CultureInfo.InvariantCulture);` before `default` (mirrors `JsonlGraphFormat`). One-home
   explanation for the egress side.
3. **C3–C5** — `Controllers/GraphController.cs`: pass `CultureInfo.InvariantCulture` to the
   `Convert.ChangeType` calls in `TryConvertLiteral`, the range-scan limits, and
   `AddProperty`; add `using System.Globalization;`; one-line pointers to C1.
4. **Tests** — `fallen-8-unittest/PropertyIngestionCultureTest.cs`: reproduce under a forced
   comma-decimal culture (`de-DE`), asserting round-trip for `double`/`single`/`decimal`,
   scan + range selection, and integer/string/DateTime regression guards. Save/restore
   `CultureInfo.CurrentCulture` in `finally`.
5. **Gate** — `dotnet build` clean (warnings are errors), full `dotnet test` green,
   including `CodeQualityTest`. No OpenAPI snapshot change (no route/doc change).

## Out of scope

Auditing every `ToString()`/`Parse` in the app, changing the wire format, or touching the
engine core or the already-correct `JsonlGraphFormat` bulk path.
