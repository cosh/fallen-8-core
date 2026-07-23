# Subgraph semantic thresholds — spec

Reframe the semantic block on subgraphs as what it mechanically is — a **vertex membership
threshold** on a filter slot — extend it to pattern-level vertex steps, and make a
registered subgraph's bound semantic state visible. Follow-up to
[element-embeddings](../../done/element-embeddings/): the semantic-traversal mechanics
(scoring rules, binding, gating) are specified there and do not change; this feature adds
slots that consume them, an echo, and the Studio restructure.

## 1 Problem

The Studio's subgraph screen presents "SEMANTIC SCORING — similarity filter/cost · pure
data, runs with dynamic code off" as a standalone section. Validated against the code, six
things are wrong or missing:

1. **The subheader lies on this screen.** `costBySimilarity` is a 400 on `PUT /subgraph`;
   the text is shared verbatim with the Path screen (`SemanticBlockEditor.tsx`), only the
   checkbox is hidden.
2. **"Scoring" is the wrong frame.** Nothing in a subgraph result is scored, ranked or
   ordered; `minScore` is a binary membership threshold. The word imports the Path mental
   model (where similarity genuinely drives Dijkstra costs).
3. **Slot ownership is invisible.** Declaratively, the block does exactly one thing: fill
   the top-level vertex pre-filter slot (`CodeGenerationHelper.TryGenerateSubGraphDefinition`).
   The UI renders it as a sibling section; the competition with the `vertexFilter` fragment
   only surfaces post-hoc as a disabled editor.
4. **A fully inert state is expressible.** Enabled block + no `minScore` + no fragments:
   the server accepts it and nothing consumes the vector — a dead vector persists in the
   recipe.
5. **Registration-time binding is stated nowhere in the UI.** The single biggest semantic
   difference from Path — the resolved vector freezes at creation, recalculation never
   re-embeds — lives only in code comments and endpoint remarks.
6. **It vanishes after creation, and "Save as stored query…" silently drops it.**
   `SubGraphSummary` has no semantic fields; the save-as-stored block is built from
   fragments/patterns only, so a form with a semantic threshold saves a template with no
   vertex filter at all, without warning.

And one capability gap (the driver for extending, not just relabeling): pattern-level
vertex steps can only do semantic filtering through compiled C# fragments
(`context.TrySimilarity`, dynamic code ON). A code-off deployment cannot express
"a vertex near the query connected via `owns` to another vertex near the query".

## 2 Goals

- One mental model: *a semantic threshold is one of the ways to fill a vertex-filter
  slot* — top level and per vertex pattern step alike — structurally, not via error strings.
- Declarative semantic pattern matching with `EnableDynamicCodeExecution=false`.
- A registered subgraph reveals its bound semantic state (never the raw vector).
- Honest per-screen wording; the inert state and the silent save-as-stored drop become
  unrepresentable.

## 3 Non-goals (with revisit triggers)

- **One semantic query per request.** No per-slot vectors, metrics or embedding names —
  every threshold in a request scores against the same bound query. Revisit when a real
  workload needs "vertex near A connected to vertex near B" (two vectors); that is the
  honest next step, not more knobs on this one.
- **No edge semantic thresholds.** Edges can carry embeddings, but no declarative edge
  threshold until a workload asks; fragments already cover it under dynamic code.
- **No result scoring/ranking.** A subgraph stays a set; no score column materializes.
- **No parameterized stored subgraph templates.** Unchanged element-embeddings non-goal,
  same trigger (a real caller needs per-invocation rebinding).
- **No path-side changes.** Paths have no patterns; the path contract is untouched.

## 4 Functional requirements

- FR-1 **Per-pattern threshold.** `PatternSpecification` gains an optional
  `semanticMinScore` (double). Meaningful only on `type: "Vertex"`; it installs a native
  vertex-filter closure over the request's traversal context with semantics identical to
  `semantic.minScore` (metric-aware direction, missing/incomparable embedding never
  matches, `VectorMath` scores bit-identical to kNN — element-embeddings README is the
  single reference).
- FR-2 **Requires the query.** `semanticMinScore` anywhere requires the request-level
  `semantic` block with a resolvable vector; the block keeps supplying exactly one query
  per request (§3).
- FR-3 **One owner per slot, per step.** `semanticMinScore` + `vertexFilter` fragment on
  the *same* step → 400. Different steps choose independently (step 1 fragment, step 3
  threshold is fine). The existing top-level rule is unchanged.
- FR-4 **Gate posture.** `semanticMinScore` is data: `CarriesInlineCode` does not consider
  it, and a request whose only filters are declarative (top-level `minScore` and/or
  pattern thresholds) compiles nothing and runs with dynamic code OFF.
- FR-5 **Producer parity.** All definition producers share
  `TryGenerateSubGraphDefinition`, so the REST endpoint, the persisted-recipe compiler
  (recalculation / WAL replay reuse the registration-bound vector — existing rule) and the
  stored-query compiler bind thresholds identically — except FR-6.
- FR-6 **Stored templates keep their posture.** A stored SubGraph template block
  containing any `semanticMinScore` is rejected at registration (400): a template has no
  semantic block, so the threshold would bind the empty context and match nothing —
  rejected loudly rather than registered dead. Mirrors the existing
  semantic-on-invocation 400.
- FR-7 **Summary echo.** `SubGraphSummary` gains an optional `semantic` object, projected
  from the persisted recipe and absent for non-semantic subgraphs:

  ```jsonc
  "semantic": {
    "embeddingName": "default",
    "metric": "Cosine",
    "dimension": 384,             // of the bound vector — never the vector itself
    "queryText": "red bicycles",  // only when registered via queryText (documents intent;
                                  // the bound vector remains the truth)
    "minScore": 0.7,              // top-level threshold, when set
    "patternThresholds": [        // vertex steps carrying thresholds
      { "pattern": "start", "minScore": 0.6 }   // patternName, or the step index as string
    ]
  }
  ```

- FR-8 **Studio restructure.** On the subgraph screen:
  - The top-level VERTEXFILTER slot and each Vertex pattern step render a mode chooser:
    *match everything / C# fragment / semantic threshold*. Slot ownership becomes
    structural; the "owned by semantic minScore" disabled-editor workaround disappears.
  - One request-level SEMANTIC QUERY section (source vector/text, embedding name, metric)
    activates when ≥ 1 slot is in semantic mode — the single place the query is defined,
    mirroring the wire contract. It states the binding once: *resolved once at creation
    and stored with the subgraph; recalculate reuses it — text is never re-embedded.*
  - The inert state is unrepresentable (a semantic query with no consuming slot cannot be
    submitted). The server stays lenient — fragments may consume `context`, which is not
    cheaply detectable, and the accept-contract is unchanged.
  - "Save as stored query…" is blocked with a reason while any semantic mode is active
    (matches FR-6) instead of silently dropping it.
  - The subgraph list/detail shows a semantic badge fed by the FR-7 echo.
  - The Path screen keeps its combined block (filter + cost genuinely share the vector
    there); the subheader text becomes per-screen instead of shared.

## 5 Errors (delta to the element-embeddings table)

| request | answer |
|---|---|
| `semanticMinScore` on an `Edge` / `VariableLengthEdge` step | 400 (explicit, not silently ignored) |
| `semanticMinScore` without a `semantic` block carrying a vector | 400 |
| `semanticMinScore` + `vertexFilter` fragment on the same step | 400 |
| non-finite `semanticMinScore` | 400 |
| stored SubGraph template registration whose block contains `semanticMinScore` | 400 |

## 6 Impact on existing features

- **Engine**: none. `VertexPattern.Vertex` is already a delegate slot native closures can
  fill. Copied subgraph vertices carry the reserved `$embedding:*` properties
  (`GetAllProperties` copies the full store), so thresholds evaluate correctly against
  copies — verified before speccing.
- **REST contract / OpenAPI snapshot**: additive (`PatternSpecification.semanticMinScore`,
  `SubGraphSummary.semantic`); regenerate via `pwsh scripts/update-openapi-snapshot.ps1`,
  additions only expected.
- **element-embeddings (living README)**: its "Subgraphs" section and errors table gain
  the delta — that README stays the ONE home for semantic-traversal rules; this feature
  ships no README of its own (spec/plan are the historical record).
- **stored-query-library**: one new registration rejection (FR-6); one-line README note.
- **Studio**: this feature *is* the restructure. The Query screen's semantic usage is the
  kNN scan — untouched. Path screen: subheader copy only. Persisted screen drafts: the
  subgraph draft shape changes (slot modes replace the block-local flags) — drafts are
  session conveniences; a migration-or-reset decision lands in the plan.
- **NL-assist**: no retrain, no RETRAIN-LOG entry. The dataset maps intent → C# fragments
  and the fragment contract is unchanged; the declarative mode is an alternative the
  assist never emits. Teaching the assist to *recommend* declarative mode would be a
  dataset enrichment with its own trigger, not this feature's.
- **Persisted recipes / WAL**: old recipes load unchanged (field absent). New recipes with
  thresholds are additive JSON; a *downgraded* build would deserialize-and-ignore the
  field, silently dropping thresholds on recalculation — accepted for the single-process
  self-hosted reality and stated here rather than engineered around.
- **Samples**: check the Studio sample graphs/drafts for references to the old block shape
  during implementation.
