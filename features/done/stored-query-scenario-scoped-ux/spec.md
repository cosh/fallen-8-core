# Stored-query UX: scenario-scoped

## Problem

Stored queries come in exactly two kinds — `Path` and `SubGraph` (`StoredQueryKind`) —
and each is only ever registered and invoked from its own scenario screen. The Studio,
however, kept a single **Stored queries** management table on the **Query** screen that
listed *both* kinds mixed together, with "Open in Path" / "Open in Subgraph" cross-links
that navigated away. That placement:

- put stored-query management on a screen (property/index scans) that has nothing to do
  with stored queries, and
- hid the kind-uniqueness — a Path author saw Subgraph entries and vice-versa.

## Behaviour

- The **Query** screen no longer hosts a Stored queries section. Its job is property and
  index scans only.
- The **Path** and **Subgraph** screens each own a **kind-scoped** Stored queries panel:
  Path lists only `Path` entries, Subgraph only `SubGraph`. Each panel is the management
  home for its kind — list, read-only source, recompile diagnostics, delete, and a **Use**
  action that selects the entry into that screen's own filter picker (no cross-screen
  navigation).
- Registration ("Save as stored query…") is unchanged and already lives on both screens,
  in the inline-fragment advanced tier where a fragment can be validated before capture.

The engine/REST contract is untouched: still one `POST/GET/DELETE /storedquery` library
per namespace, still `Path` | `SubGraph`. This is a Studio-only relocation.

## Impact on existing features

- **Engine / REST / OpenAPI snapshot:** none — no controller, route, or model change.
- **MCP:** none — no REST surface change, so `McpRestCoverageTest` is unaffected.
- **Studio UI:** `StoredQueriesPanel` gains a required `kind` prop and an `onUse` callback
  (replacing the `useNavigate`/`subgraphPrefill` cross-links). `QueryScreen` drops it;
  `PathScreen`/`SubgraphScreen` render it. The one-shot `SubgraphPrefill` store slot is
  removed (its only producer was the Query panel's "Open in Subgraph").
- **Docs:** `docs/studio.md` Query/Path/Subgraph/Dashboard sections updated; the affected
  screenshots (`screen-query`, `screen-path`, `screen-subgraph-builder`) regenerated.
- **NL-assist dataset:** none — stored-query REST payloads are unchanged.
