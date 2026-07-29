# NL-assist retrain log

The running list of product changes that the NL-assist model has not been trained for yet.
Features append an entry here (CLAUDE.md feature workflow, cross-feature impact check)
instead of re-litigating "does this need a retrain?" per feature; the next fine-tune run
drains every PENDING entry in one pass — **phase 2 (dataset generation) starts by reading
this file**, and an entry is closed by recording the model version that absorbed it.

Add an entry when a change touches the delegate-fragment surface the model drafts against:

- a delegate kind is added/removed, or a slot changes which kind it requests,
- the fragment idiom or type surface changes (`type-model.json`, snippets, member names),
- the NL prompt contract changes (`buildGenerationPrompt` / `buildRefinePrompt`),
- a new scenario class appears that the model should be able to draft.

Do NOT log general engine/API work that leaves fragments unchanged.

Entry format: heading `date — feature — status` (PENDING → CLOSED), then what changed,
required dataset/scenario changes, prompt/eval impact, and `Closed by:` once absorbed.

---

## 2026-07-22 — subgraph-typed-filters — PENDING

**Contract change:** every subgraph filter slot is typed now. Top-level
`vertexFilter`/`edgeFilter` are `VertexFilter`/`EdgeFilter` kinds (`(VertexModel v)` /
`(EdgeModel e)`), the per-step `graphElementFilter` slot is gone. No UI slot requests
`GraphElementFilter` drafts anymore; the kind survives only on `/delegates/validate`.

**Dataset:** retarget/reweight the `GraphElementFilter` rows (`dataset-gen/generate.ts`
`FILTER` kinds) toward `VertexFilter`/`EdgeFilter`; add scenarios exercising the newly
reachable typed members in top-level slots (degree/adjacency on vertices,
`SourceVertex`/`TargetVertex` on edges).

**Prompt/eval:** `eval/fixture.ts` comment claiming "/subgraph's vertexFilter/edgeFilter
take a GraphElementFilter (AGraphElementModel)" is stale — its vertex-scope mapping should
key on the typed kinds; `eval/eval-set.json` GraphElementFilter rows still validate but no
longer represent a real slot.

**Follow-up once drained:** remove the GraphElementFilter kind end to end (validate
endpoint, UI kind table, engine delegate) — trigger documented in
`features/done/subgraph-typed-filters/spec.md`.

**Closed by:** —

## 2026-07-25 — plugin-registration — PENDING

**Contract change:** a new authoring surface lands in F8 Studio — **whole-type C# plugins**
(feature plugin-registration), distinct from the existing fragment (filter/cost body) surface.
Users author a complete type implementing a category contract (`IShortestPathAlgorithm`,
`ISubGraphAlgorithm`, `IGraphAnalyticsAlgorithm`, or the new `IGraphFunction`) and register it to a
namespace; the editor compiles/contract-validates via `POST /plugins/{category}/validate`.

**Dataset:** the current corpus is fragment-shaped (a lambda body for a typed slot). Whole-type
authoring is a different generation target — the model must emit a full class with the `IPlugin`
members, the correct usings, and the contract method (e.g. `IGraphFunction.TryInvoke` returning a
`GraphFunctionResult` over `IFallen8` reads). Add a `PLUGIN` generation kind with per-contract
scaffolds and few-shot whole-type examples; do **not** retarget the fragment rows (both surfaces
coexist).

**Prompt/eval:** NL-assist for plugin authoring ships in v1 against a general model (the whole-type
prompt scaffolding lives in Studio); the fine-tune should absorb whole-type examples so the local
model can draft them. Add eval scenarios that compile a generated plugin through the plugin-aware
validate endpoint (not `/delegates/validate`).

**Closed by:** Pipeline TOOLING is wired for whole-type plugins (not yet a fine-tune RUN — that
remains the operator's to execute against a live F8 + GPU):
- `dataset-gen/generate.ts` now emits a PLUGIN generation kind alongside the untouched fragment
  rows — per-contract seeds (function + algorithm Path/SubGraph/Analytics), each a whole type
  built from `buildPluginGenerationPrompt` + `scaffoldFor`, compile-gated through
  `POST /plugins/{category}/validate`, with a coverage guard requiring ≥1 plugin row per contract
  and the drift hash extended to `plugin/nl/pluginPrompt.ts` + `plugin/scaffolds.ts`.
- `feedback/consolidate.ts` now ingests the plugin panel's `{ kind:"plugin", … }` captures
  (fixing the prior mis-ingest that sent an empty body to `/delegates/validate`): plugin rows are
  read via their own fields, re-validated via `validatePlugin`, and written as whole-type corpus
  rows; fragment captures are unchanged; malformed lines are skipped, never abort.
- `eval/plugin-eval-set.json` + `eval/baseline.ts` add a held-out, COMPILE-ONLY plugin eval path
  (the `/subgraph` element-set gate does not apply to whole types).
- `train/train-config.phi4-f8*.json` raise `maxSeqLength` 2048→4096 so a whole plugin type is not
  truncated; `train_lora.py` is shape-agnostic (reads only `messages`) and needed no change.
Deferred (honest): richer Path/SubGraph algorithm BODIES beyond the compiling skeleton are left to
the operator's bootstrap + captured-feedback loop, where they are verified against the live
validator; hand-authoring complex traversal/subgraph bodies here without a live compile gate would
only add rows that silently drop. Entry stays PENDING until an actual fine-tune run absorbs these
examples — close it then with the produced model version.

## 2026-07-26 - plugin-registration (NL draft hardening) - PENDING

**Why:** in the field the local model drafting an `IGraphFunction` dropped
`using NoSQL.GraphDB.Core.Plugins;` (so `IGraphFunction`/`GraphFunctionResult` "could not be
found") and wrote a LINQ body without `using System.Linq;`, on top of body mistakes
(`out string` on an `object` bag, a malformed expression). The prior function corpus was three
trivial label scans, none exercising a whole-graph property scan or LINQ.

**Prompt (drift hash bumped -> regenerate):** `buildPluginGenerationPrompt` now pins the required
using directives explicitly and tells the model to add `System.Linq` for a LINQ body. This edits
`plugin/nl/pluginPrompt.ts`, one of the drift-hash sources, so the committed
`dataset/dataset.meta.json` is now stale by design; the next run regenerates it.

**Dataset:** two new READ-ONLY function seeds in `dataset-gen/generate.ts` exercise the
property-predicate shape the fragment surface can't express and the corpus lacked:
`VerticesWithPropertyValue` (`GetAllProperties().Values.Any(...)`) and `VerticesAboveAge`
(`Where(v => v.TryGetProperty(out int age, "age") && age > min)`), both importing `System.Linq`.

**Eval:** `eval/plugin-eval-set.json` adds `fn-property-equals`, a held-out compile-only row for a
predicate function (a distinct phrasing from the seeds).

**Not blocking:** the Studio runtime already re-anchors every draft to the scaffold's required
usings (+ `System.Linq` when the body uses LINQ) via `pluginPrompt.ts:ensureRequiredUsings`, so the
"could not be found" class is fixed today without the model. The retrain improves the body quality
that deterministic import-repair cannot (the `out object` / malformed-expression mistakes).

**Closed by:** (open)

## 2026-07-29 - element-fulltext-match (new member + string-predicate scenarios) - PENDING

**Why:** target prompt class (feature `features/open/element-fulltext-match/`): "Filter for
Company nodes where the name contains 'Tech' and the industry field ends with 'Solutions'",
and "any field mentions 'Tech'". The feature adds ONE `AGraphElementModel` member the model
must learn - `AnyPropertyValueMatches(Func<string, bool> valuePredicate)` (match semantics
stay in the BCL; deliberately no match-kind enum; "Value" is in the name because
`EdgePropertyFilter` already trains a bare string predicate over property NAMES) - a fragment
type-surface change (trigger 2).
The corpus also lacks the scenario classes (trigger 4): string rows cover only
single-predicate starts-with/ends-with on a value (`prop-str-starts`/`prop-str-ends`),
"contains" exists only for edge-property NAMES (`epf-contains`), and no row combines a label
with more than one string predicate.

**Corpus-neutral hardening in the same feature:** `TryGetProperty<T>` returns false on a type
mismatch instead of throwing, so the existing trained per-field idiom becomes safe as-is - no
row changes needed for it.

**Tooling wired on the feature branch (not yet a fine-tune RUN - that remains the
operator's, against a live F8 + GPU):**

- `dataset-gen/generate.ts` FILTER rows: `prop-str-contains` (the existing
  `TryGetProperty` idiom extended to `Contains`, over a new `substrings` pool),
  `label-and-2str` (label plus two ANDed string predicates, including the exact target
  phrasing), `any-prop-contains` (`AnyPropertyValueMatches(s => s.Contains("Tech"))`) and
  `any-prop-contains-ci` (`StringComparison.OrdinalIgnoreCase`). Rows compile-gate through
  `/delegates/validate`, so generation must run against an engine WITH this feature.
- **Prompt contract change (drift hash bumped -> regenerate):** `nl/prompt.ts`'s
  single-lambda rule forbade any second lambda, which would fight the new member's
  predicate argument; it now forbids inline-INVOKED lambdas and explicitly allows a
  lambda passed as a member ARGUMENT (generation and refine prompts). Together with
  `type-model.json` (member added, `TryGetProperty` doc now "missing, not a T, or null")
  and `snippets.ts` (new "Any property value contains" snippet), three drift-hash sources
  changed - the committed `dataset.meta.json` is stale by design; the next run regenerates.
- `eval/eval-set.json` held-out rows: `vf-compound-strings` (the target phrasing, must NOT
  draft the any-property member), `vf-any-prop-contains` ("any field mentions Tech"),
  `vf-value-not-name` (value phrasing must use the member - the existing `epf-*` rows keep
  pinning the property-NAME side, so the two bare-string-predicate surfaces are held apart).

**Closed by:** -
