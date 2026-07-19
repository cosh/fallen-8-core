# Property ingestion/egress culture — Specification

> **Status:** spec. A correctness fix: the REST property/literal round-trip must be
> locale-independent. Follow the feature workflow in the repository root `CLAUDE.md`.

## 1. Problem

The REST layer converts a `PropertySpecification` value (a string + a .NET type name) to a
typed value on the way in, and back to a string on the way out, using the **host's
`CultureInfo.CurrentCulture`** instead of a fixed culture. On any comma-decimal locale
(e.g. `de-DE`, where `.` is a group separator) this corrupts every non-integer numeric
property and scan literal:

- **Ingest:** `Convert.ChangeType("0.8", typeof(double))` on `de-DE` parses `"0.8"` as
  **`8.0`** (the `.` is read as a thousands separator).
- **Egress:** a stored `double 0.8` rendered with `value.ToString()` on `de-DE` becomes
  **`"0,8"`** — a comma the JSON consumer (and any re-`PUT`) cannot round-trip.

So `PUT /vertex {"age": ... , "weight": 0.8 (System.Double)}` followed by `GET /vertex`
does not return `0.8` on a comma-decimal server. `float`, `decimal`, and range/scan
literals are affected identically. Integers and strings are not (no decimal separator).

Discovered while seeding a fixture for the `nl-assist-finetune` phase-4 semantic eval on a
German-locale box: a `weight: 0.8` edge property came back as `8`.

## 2. Blast radius

All in `fallen-8-core-apiApp`; the engine core stores the already-typed value and is
unaffected. Five conversion sites, all currently culture-sensitive:

| # | Site | Direction |
|---|---|---|
| C1 | `Helper/ServiceHelper.cs` `CreateObject` — `Convert.ChangeType(value, type)` | ingest (vertex/edge create) |
| C2 | `Controllers/Model/AGraphElement.cs` `FormatPropertyValue` — `default: value.ToString()` | egress (vertex/edge read) |
| C3 | `Controllers/GraphController.cs` `TryConvertLiteral` — scan literal | ingest (property scan) |
| C4 | `Controllers/GraphController.cs` range-scan `LeftLimit`/`RightLimit` | ingest (range scan) |
| C5 | `Controllers/GraphController.cs` `AddProperty` — property value | ingest (property update) |

## 3. There is already a correct convention

The bulk-import/export path (`Helper/JsonlGraphFormat.cs`) *already* parses and formats
every scalar with `CultureInfo.InvariantCulture` (explicit `Single`/`Double`/`Decimal`
cases plus an `IFormattable f => f.ToString(null, InvariantCulture)` fallback), and
`AGraphElement.FormatPropertyValue` already uses invariant for its `Single[]` and
`DateTime` cases. The REST scalar path is simply the outlier that was never aligned. This
fix makes the whole property surface consistent — the wire format is data interchange and
must never carry the server's locale.

## 4. Fix

Use `CultureInfo.InvariantCulture` at C1–C5, in both directions:

- C1/C3/C4/C5: pass `CultureInfo.InvariantCulture` to `Convert.ChangeType(value, type, …)`.
- C2: add an `IFormattable f => f.ToString(null, CultureInfo.InvariantCulture)` case before
  the `default`, mirroring `JsonlGraphFormat` (covers `double`/`float`/`decimal`; the
  explicit `Single[]`/`DateTime` cases still win; genuinely non-formattable objects keep
  `ToString()`).

The one-home explanation lives on `CreateObject` (ingest) and `FormatPropertyValue`
(egress); the other sites carry a one-line pointer. No wire-format, contract, or route
change → no OpenAPI snapshot change.

## 5. Testing

MSTest, run under a forced comma-decimal culture so the bug is reproduced deterministically
on any CI machine (save/restore `CurrentCulture` in `finally`):

- **Round-trip:** `AddVertex` a `System.Double` `0.8` (and a `System.Single`, a
  `System.Decimal`) under `de-DE`, then `GetVertex`/`GetGraph` and assert the value parsed
  to the intended number and re-serializes to the invariant string `"0.8"` — not `8`/`"0,8"`.
- **Scan + range:** a property scan and a range scan whose limits are decimals select the
  right elements under `de-DE`.
- **Regression guards:** integer and string properties are byte-identical across cultures;
  a `DateTime` property still round-trips (already invariant).
- A test asserting the four `Convert.ChangeType` call sites and the formatter agree with the
  invariant expectation (culture-parameterized where practical).

## 6. Non-goals

- No change to the JSON wire shape, routes, or the engine core.
- Not touching `JsonlGraphFormat` (already correct) or the already-invariant `DateTime` /
  `Single[]` egress cases beyond leaving them intact.
- Not a general audit of every `ToString()` in the app — only the property/literal
  round-trip that carries user data through the REST boundary.
