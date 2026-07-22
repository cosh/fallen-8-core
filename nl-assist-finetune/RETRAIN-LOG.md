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
