# Element fulltext match - plan

## Phase 1 - engine

- `TryGetProperty<T>`: is-pattern instead of the cast. Tests pin: mismatch is `false`
  (was throw); null-valued property is `false` for reference and `Object` targets (was
  `true` + null); exact-type hit unchanged; `<Object>` passthrough unchanged; embedding
  accessors stay green.
- `AnyPropertyValueMatches(Func<String, Boolean>)` on `AGraphElementModel`, walking the compact
  property store by reference (no `GetAllProperties()` snapshot), feeding only
  string-typed values, skipping the `$embedding:` / `$embeddingModel:` reserved prefixes.
- Tests: predicate sees only string values (mixed-type element); reserved-key skip
  (element whose embedding model stamp contains the query still returns `false`); no
  properties; null predicate is `false`; vertex and edge both covered; predicate
  exceptions propagate (documented: the member adds no throw of its own).

## Phase 2 - fragment end-to-end

- A `VertexFilter` and an `EdgeFilter` fragment using `AnyPropertyValueMatches` with a nested
  lambda and `StringComparison.OrdinalIgnoreCase` compile via `/delegates/validate` and
  filter correctly through `/path` and `/subgraph` (proves the existing usings suffice -
  no codegen change expected).
- A fragment reading a property with the wrong `out` type filters the element instead of
  faulting the traversal (pins the `TryGetProperty` fix at the REST level).

## Phase 3 - surface sweep (spec impact table)

- Studio: `AnyPropertyValueMatches` into `fallen-8-web-ui/src/delegate/type-model.json`;
  update the `TryGetProperty` doc line (false on mismatch); mirror in the web-ui spec
  §6.2 checklist; snippet in `snippets.ts` only if it earns its place; rebuild the bundle.
- MCP: extend the fragment-authoring guidance strings that teach `TryGetProperty`
  (`SubgraphTool` and wherever else `grep TryGetProperty fallen-8-mcp` hits).
- Docs: document the member and the mismatch fix in `docs/src/content/docs/delegates.mdx`;
  sweep `path-finding.mdx` / `stored-queries.mdx` examples for a place where the new idiom
  reads better. Docs build must stay green. No new page, no new README bullet.
- Verify the OpenAPI snapshot is byte-identical (script prints an empty diff).

## Phase 4 - close-out

- `nl-assist-finetune/RETRAIN-LOG.md` entry 2026-07-29 is already logged and stays
  PENDING until a fine-tune run drains it (the new-member rows need this feature's
  `/delegates/validate`, so generation runs after merge).
- Feature `README.md` (living doc), move `features/open/element-fulltext-match/` to
  `features/done/`.
