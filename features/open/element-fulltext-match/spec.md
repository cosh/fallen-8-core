# Element fulltext match - spec

## Problem

Inside a delegate fragment (`VertexFilter`, `EdgeFilter`, and friends) the only property
access is one named key at a time: `TryGetProperty(out T value, "key")`. The target prompt
class - "Filter for Company nodes where the name contains 'Tech' and the industry field
ends with 'Solutions'", and its generalisation "any field mentions 'Tech'" - hits three
walls in that scope:

1. **The per-field idiom can crash.** `TryGetProperty<string>` CASTS the stored value: if
   `name` holds an `int` on some element, the fragment throws `InvalidCastException`
   mid-traversal instead of filtering the element out. This contradicts the repo's own
   Try* convention ("return false for expected not-found/invalid cases"), and the engine's
   embedding accessors already work around exactly this footgun (`TryGetProperty<Object>`
   plus an `is` check).
2. **"Any property of this element" has no idiom.** The closest fragment shape is
   `ge.GetAllProperties().Values.Any(x => x is string s && s.Contains("Tech"))` - verbose,
   easy to mis-draft, and it allocates an `ImmutableDictionary` snapshot **per element per
   evaluation** inside hot traversal loops.
3. **That shape is also wrong.** `GetAllProperties()` exposes the reserved embedding
   entries; the `$embeddingModel:<name>` stamp is a *string* (e.g. "nomic-embed-text"), so
   an any-property `Contains("text")` false-positives on every element that merely carries
   an embedding stamp.

This is strictly about the element in the delegate's hand. The graph-wide scan surface
(`GraphScan`, the index scans, REST `/scan/*`, the MCP search tool) is untouched.

## Behaviour after the change

Design principle: it is .NET - `System.String` and `StringComparison` already carry every
match semantic (contains / starts / ends / equals, case-insensitivity), and both fragment
authors and the NL model know them. So no match-kind enum and no per-field convenience
member; instead, fix the footgun and add the one primitive the BCL cannot provide.

**1. `TryGetProperty<T>` returns false on a type mismatch** (is-pattern instead of a
cast). The per-field half of the prompt class then needs nothing new - the already-trained
idiom simply becomes safe. Two behaviour notes, called out honestly:

- a mismatched `T` was an `InvalidCastException`, now `false` (strictly softer);
- a property explicitly stored as `null` reported `true` with a `null` result for
  reference-typed `T`, now `false` (`null is T` is false). A null value reads as "no
  value", which the Try* contract arguably always meant.
- The engine paths that genuinely need null-PRESENCE (transaction undo/prior-value
  capture, batch conflict validation) move to an internal presence-preserving
  `TryGetPropertyRaw` - a rollback must restore a null-valued key, not remove it, and a
  batch set over one must stay a conflict. Both pinned in `ElementFulltextMatchTest`.
- Free bug fix surfaced by the change: `GraphScan` over an element with a null-valued
  property called `null.Equals(...)` inside the parallel scan (NullReferenceException);
  a null value is now a clean skip.

**2. One new member on `AGraphElementModel`** (so every fragment kind and plugins get it):

```csharp
public Boolean AnyPropertyValueMatches(Func<String, Boolean> valuePredicate);
```

"Value" is in the name deliberately: the fragment ecosystem already has a bare string
predicate over property NAMES (the `EdgePropertyFilter` kind drafts
`(p) => p.Contains("work")` against names), so a plain `AnyPropertyMatches(s => ...)`
would not say which of the two `s` is. This predicate receives property values, never
names.

The target prompt class becomes:

```csharp
return (v) => v.Label == "company"
           && v.TryGetProperty(out string name, "name") && name.Contains("Tech")
           && v.TryGetProperty(out string ind, "industry") && ind.EndsWith("Solutions");

return (v) => v.AnyPropertyValueMatches(s => s.Contains("Tech", StringComparison.OrdinalIgnoreCase));
```

Semantics:

- The predicate sees **string-typed property VALUES only** - names never reach it; match
  semantics (kind, case, culture) live entirely in the caller's BCL calls.
- **Reserved keys are skipped**: `$embedding:` / `$embeddingModel:` entries never reach
  the predicate (fixes wall 3).
- **Allocation-free walk** over the compact copy-on-write property store, the same
  single-writer / lock-free-reader snapshot discipline as `TryGetProperty`. A
  capture-free predicate lambda is cached by the compiler, so the common fragment shape
  allocates nothing per element.
- A null predicate is `false`; a throwing user predicate propagates (the member adds no
  throw of its own).
- **No codegen change**: the fragment compilation context already has `using System;`
  (for `StringComparison`) and `using System.Linq;`.

## Impact on existing features

| Feature | Impact | Handling |
|---|---|---|
| engine callers of `TryGetProperty<T>` | Mismatch: throw becomes `false`; null value: `true`+null becomes `false` | Pinned by tests; the embedding accessors' defensive `<Object>` + `is` pattern keeps working (optionally simplified) |
| web-ui (Studio) | Delegate-editor IntelliSense is hand-maintained | Add `AnyPropertyValueMatches` to `fallen-8-web-ui/src/delegate/type-model.json` and update the `TryGetProperty` doc line (false on mismatch); mirror in the web-ui spec §6.2 checklist; bundle rebuilt |
| nl-assist-finetune | One new member (type-surface change) plus new scenario classes; existing `TryGetProperty` string rows stay valid and become safe | PENDING entry 2026-07-29 in `RETRAIN-LOG.md`; new-member rows compile-gate through `/delegates/validate`, so generation waits for this feature to land |
| mcp-server | Fragment-authoring guidance in tool descriptions teaches the `TryGetProperty` idiom (`SubgraphTool` and friends) | Mention `AnyPropertyValueMatches`; no new REST op, so no coverage-gate entry |
| docs-site | `delegates.mdx` documents the fragment surface; `path-finding.mdx`/`stored-queries.mdx` carry fragment examples | Document the member and the mismatch fix in `delegates.mdx`; no new page, no new README bullet (the existing delegates entry covers the surface) |
| openapi-10 | No route or controller XML-doc change expected | Snapshot untouched; verify via the script's diff |
| stored-query-library / save-games | Additive member; persisted fragments recompile unchanged (a mismatch-throwing fragment now filters instead of crashing) | None |
| architecture docs | No new channel or deployable | No diagram change |

## Non-goals (with revisit triggers)

- **A `StringMatchKind` enum and `PropertyMatches`/`AnyPropertyContains` convenience
  members** (an earlier draft of this spec had them). Rejected as BCL duplication: the
  enum would re-teach what `System.String` already expresses, cost a codegen using, three
  type-model entries, and a bespoke API the NL model must learn instead of leaning on C#
  it already knows. Revisit only if eval shows the local model measurably fumbling the
  `TryGetProperty`-plus-BCL idiom or the nested predicate; then add the narrowest
  convenience that fixes the measured failure.
- **A graph-wide fulltext scan** (engine scan + `POST /scan/graph/fulltext` + MCP search
  mode). The ask is the delegate scope. Revisit when a non-code client needs it
  declaratively (MCP search tool without the `code` capability, or a Studio global
  search box).
- **A name-aware predicate** (`Func<string, string, bool>` over name and value, or a
  name-matching sibling). The prompt class is content search; the common case would carry
  a discarded parameter forever. Name-scoped matching stays expressible via
  `GetAllProperties()` (allocating, but rare). Revisit when a real prompt class needs
  "any property named like X whose value ..." at traversal speed.
- **Feeding non-string values to the predicate via `ToString()`** ("any property mentions
  42"). Revisit if a real dataset needs it.
- **Changing `GetAllProperties()`** to hide reserved keys. Existing public behaviour,
  separate discussion; the new member simply does the right thing.
