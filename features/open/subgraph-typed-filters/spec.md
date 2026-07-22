# Subgraph typed filters — spec

## Problem

The subgraph contract leaks an internal abstraction and it confuses users (surfaced by the
Studio UI, but the UI renders the contract faithfully — the contract is the bug):

1. **Top-level `vertexFilter`/`edgeFilter` are graph-element-typed.** The engine's
   `SubGraphDefinition.VertexFilter`/`EdgeFilter` are `GraphElementPattern`, and the
   algorithm only ever reads the `GraphElement` delegate from them. So the REST fragment
   for a *vertex* filter receives `AGraphElementModel ge` — and the Studio delegate editor
   truthfully opens a GRAPHELEMENTFILTER for a slot labeled VERTEXFILTER.
2. **Every pattern step carries a redundant `graphElementFilter`** alongside its
   type-specific filter (`vertexFilter` on vertex steps; `edgeFilter` + `edgePropertyFilter`
   on edge / variable-length-edge steps). `VertexModel` and `EdgeModel` derive from
   `AGraphElementModel`, so the typed filter expresses everything the GE filter can.
   Two slots for one job.

## Behaviour after the change

One filter slot per role, typed for what it actually receives:

| Slot | Fragment shape |
|---|---|
| top-level `vertexFilter` | `return (v) => …;` — `Delegates.VertexFilter` (`VertexModel`) |
| top-level `edgeFilter` | `return (e) => …;` — `Delegates.EdgeFilter` (`EdgeModel`) |
| vertex step `vertexFilter` | unchanged (`VertexModel`) |
| edge / var-length step `edgeFilter`, `edgePropertyFilter` | unchanged (`EdgeModel` / `string`) |
| any step `graphElementFilter` | **removed** |

Engine model:

- `SubGraphDefinition.VertexFilter : Delegates.VertexFilter`,
  `EdgeFilter : Delegates.EdgeFilter` (plain delegates, not patterns).
- `GraphElementPattern` (the layer that existed only to carry the `GraphElement` delegate)
  is deleted; `VertexPattern`/`EdgePattern` derive from `APattern` directly.
- The semantic `minScore` pre-filter binds as a `Delegates.VertexFilter` (the helper's
  separate `GraphElementFilter` field goes away; the path slot already used `VertexFilter`).

Wire compatibility:

- Top-level fragments written as `return (ge) => ge.Label == …;` **still compile** — the
  lambda's parameter type is inferred from the target delegate, and `Label` & friends live
  on the base type. Typed slots are strictly more expressive (vertex-only members become
  available).
- `graphElementFilter` on a pattern is no longer part of the contract. Old persisted
  recipes / stored-query blocks that carry it deserialize (unknown members are ignored) but
  **silently lose that one filter** on replay/recompile. Accepted for the single-process,
  self-hosted reality — the fix is to re-save the query with the typed filter. No
  compat shim.

## Impact on existing features

| Feature | Impact | Handling |
|---|---|---|
| web-ui (Studio) | Top-level slots were GraphElementFilter-typed; steps had a redundant GE slot | Slots retyped / removed in this feature; bundle rebuilt |
| stored-query-library | SubGraph template blocks flow through `PatternSpecification`; legacy blocks with `graphElementFilter` lose that filter on recompile | Documented above; no shim |
| element-embeddings / embedding-provider | `semantic.minScore` pre-filter was materialized twice (VertexFilter + GraphElementFilter) | Unified on `VertexFilter`; wire contract unchanged |
| wal-subgraph-support / save-games | Persisted recipes recompile through the same codegen path; `(ge)`-named top-level fragments still compile | Legacy-fragment compile pinned by test |
| nl-assist-finetune / delegate-model-variants | Eval fixture + dataset generation still target the GraphElementFilter kind; the fixture's comment that "/subgraph's vertexFilter/edgeFilter take a GraphElementFilter" is now stale | NOT changed here (active work); validate endpoint keeps the kind so eval runs stay green — next steps are the owner's call |
| openapi-10 | Snapshot loses `graphElementFilter` and changes doc text | Regenerated; removals deliberate |

## Non-goals (with revisit triggers)

- **Removing the `GraphElementFilter` delegate kind from `/delegates/validate`** (and
  `Delegates.GraphElementFilter` from the engine). The NL-assist fine-tune eval pipeline
  (`nl-assist-finetune/eval`, feature delegate-model-variants) still validates
  GraphElementFilter fragments through that endpoint, and the Studio kind table must stay
  total over the endpoint's kinds. Revisit when the fine-tune dataset/eval drops the kind —
  then delete the validate entry, the UI kind, and the engine delegate together.
- **A migration shim for persisted `graphElementFilter` fragments** (rejecting them with
  400, or AND-ing them into the typed slot). Revisit only if a real save-game with such
  recipes must survive an upgrade unedited.
