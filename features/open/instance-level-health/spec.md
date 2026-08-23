# Instance-level health - Specification

> **Status:** OPEN (specced 2026-08-23). Studio-only change; no engine and no REST work.

## 1. Why

The Connect screen's Instances table has a `health` column. On the owner's own `npm run env:up`
instance it read `0 v · 0 e` while the instance held five namespaces and thousands of elements.
Measured on that instance, 2026-08-23:

```
GET /status  -> {"vertexCount":0,"edgeCount":0,"apiKeyRequired":false,"authenticated":false, ...}
GET /ns      -> Movie 191v/1697e · default 0v/0e · f8 1013v/1774e · unify 91v/115e · "wind farm" 199v/344e
```

The cell is not lying about *a* graph, it is answering a different question than the one its column
asks. The chain:

- `ConnectScreen.tsx:48-79` (`InstanceHealth`) probes `getStatus(instance)` with the **raw registry
  record** from `useRegistry()`.
- `endpoints.ts:111` sends `/status` at the default `scope: "namespace"`.
- `client.ts:102-107` (`scopedPath`) prefixes `/ns/{namespace}` only when `instance.namespace` is
  set. A raw registry record never carries it (`types.ts:52-57`: it is bound in `useInstanceStore()`,
  `registry.ts:280-298`, which this row deliberately does not use because the row must also describe
  instances that are not active). So the request goes to the bare `/status`, which aliases the
  reserved `default` namespace, and `AdminController.cs:254-255` answers with that ONE engine's
  counts.

Two defects fall out of it:

1. **Wrong altitude.** A Fallen-8 is a collection of namespaces
   ([graph-namespaces](../../done/graph-namespaces/)); an *instance* row must therefore describe the
   instance, not one reserved namespace inside it. The Namespaces panel further down the same screen
   already showed the real numbers, which is what makes the row's `0` read as a broken instance
   rather than as a scoping choice.
2. **A silent default-only count.** Nothing in the cell says *which* graph the number belongs to, so
   there is no reading of `0 v · 0 e` that could have told the operator where to look. The owner's
   report went straight to the wrong hypothesis - a missing API key - because an empty instance and
   a scoped count are indistinguishable in that cell. (Auth was never involved: the row rendered
   counts at all, and a key-secured instance renders `unauthorized` instead.)

## 2. What this feature is, in one sentence

Make the Instances row report the **instance**: `N ns · V v · E e` summed over `GET /ns`, and never
print a bare count that silently means "the default namespace only".

## 3. Decisions

| Question | Decision | Rejected, and why |
|---|---|---|
| Where do the counts come from? | **`GET /ns`**, which already returns per-namespace counts, the inventory and the quota. Summed client-side. | A new aggregate field on `/status`: server work for a number the wire already carries, and it would have to pick a scope for a route that is namespace-scoped by contract. |
| Does `/status` stay? | **Yes, as the probe.** It is the only `[AllowAnonymous]` route (`AdminController.cs:224`), so it is the only thing that can tell `unreachable` from `unauthorized` on a key-secured instance. `/ns` is fetched only once `/status` says authorized, so the row never fires a request it knows will 401. | Probing with `/ns` alone: on a keyed instance every row would read `unreachable` when the real answer is `unauthorized`, losing the CORS hint and the wrong-key diagnosis. |
| What about a namespace the server did not load? | Its counts are `null`, never 0 (`types.ts:110-117`). The sum covers the loaded ones and is marked **`>=`**, with the tooltip naming how many did not report. If NO namespace reported, both counts render the absent glyph (`-`), not `0`. | Adding nulls as zeros: exactly the class of lie this feature exists to remove. Hiding the row's counts entirely: throws away the part that is known. |
| What about a server that predates namespaces (`/ns` 404s)? | Fall back to the `/status` counts with **no `ns` segment**: on such a server the bare paths ARE the whole graph, so the number is instance-level already. Same 404 test the Namespaces panel uses (`NamespacesPanel.tsx:186-188`). | Rendering `unreachable`: the instance is up and usable. |
| And if `/ns` fails for any other reason? | Fall back to the `/status` counts **labelled `default:`**. The row states the scope it is actually reporting instead of implying an instance total. | A bare count: reintroduces defect 2 in the degraded path, which is precisely where an operator is already confused. |
| Does anything else change? | **No.** Only this cell. The namespace-scoped surfaces (top-bar health chip, dashboard tiles, first-run gate, Namespaces panel) are correct as they are: they describe the active namespace and say so. | Adding totals to the Namespaces panel header too: a second home for the same number ("one home per explanation"), and its `N / 10,000 namespaces` line answers the quota question, not the size question. |

## 4. Behaviour

The cell has one state machine, evaluated top to bottom. `probe` is `GET /status`, `inventory` is
`GET /ns` (Fallen-8 scope, enabled only in state 4):

| # | Condition | Renders | Notes |
|---|---|---|---|
| 1 | probe pending | `checking...` | unchanged |
| 2 | probe error / no body | `unreachable` (+ CORS hint when cross-origin) | unchanged |
| 3 | probe says a key is required and we are not authenticated | `unauthorized: ...` | unchanged, both wordings (bearer / api key) |
| 4 | inventory pending | `checking...` | the row does not flash a default-only number on the way to the real one |
| 5 | inventory ok | `N ns · V v · E e` | `V`/`E` summed over entries that report counts |
| 5a | ...and some entry reported no counts | `N ns · >=V v · >=E e` | tooltip: how many of the N did not report |
| 5b | ...and NO entry reported counts | `N ns · - v · - e` | the absent glyph, never `0` |
| 6 | inventory 404 | `V v · E e` from the probe | pre-namespace server; tooltip says so |
| 7 | inventory failed otherwise | `default: V v · E e` from the probe | tooltip names the failure |

`N` is the size of the inventory, always exact (it is the catalog, not the residency filter), which
is why only the counts carry the `>=`.

Numbers use the existing `formatCountOrDash` / `formatExact` grouping (`lib/format.ts`), so
`1494 -> "1,494"`, and the absent glyph is the shared `ABSENT`, so this cell cannot spell "absent" a
second way.

### 4.1 Where the aggregation lives

One pure module, `src/lib/namespaceTotals.ts`, with the summation and the label/tooltip composition;
the component renders what it returns. That keeps the honesty rules (null is not zero, `>=` when
partial, the absent case) unit-testable without a DOM, and gives the next surface that wants an
instance total one place to take it from rather than a second summing loop.

The cell itself moves out of the screen into `src/components/InstanceHealth.tsx`: it now owns two
queries and a seven-state machine, which is a component's job rather than a helper inside a screen,
and it lets the DOM test render the cell alone instead of dragging the configuration surface and the
router in behind it.

### 4.2 No extra network cost for the active instance

The inventory query is keyed `[instance.id, "namespaces"]` with the **raw** id, which is the key
`AppShell.tsx:182` and `NamespacesPanel.tsx:124` already use (both take the raw record: `/ns` is
Fallen-8-level). So for the active instance the row rides the existing cache row and adds no request;
only a non-active instance costs one, and only while the Connect screen is open.

## 5. Impact on existing features

Swept per CLAUDE.md step 5.

| Layer / feature | Impact |
|---|---|
| Engine, REST, OpenAPI snapshot | **None.** No server change; no new route, no changed response. The snapshot and `McpRestCoverageTest` are untouched. |
| MCP server, integrations | **None.** Neither reads this cell. |
| [graph-namespaces](../../done/graph-namespaces/) | The row stops presenting `default` as the instance. Terminology now matches the feature's own model (Fallen-8 = collection of namespaces). |
| [namespace-startup-load](../../done/namespace-startup-load/) | Its "null count is not zero" rule now also binds an aggregate. Encoded as rules 5a/5b and pinned by tests. |
| [standalone-ui](../../done/standalone-ui/) managed vs personal instances | Unchanged: the cell is per row and reads the same for both. The managed same-origin `local` row (auth `none`, never persisted) is the one in the owner's report and is fixed by the same change. |
| [studio-embeddable](../../done/studio-embeddable/) `lockInstances` | Unchanged: the health cell renders in both modes; only the register/edit/remove affordances are gated. A `bearer` instance resolves its token through the same transport, so the inventory call inherits it. |
| Studio e2e | `studio.spec.ts:98` and `:495` assert the `unreachable` / wrong-key wordings; both are preserved deliberately. No e2e asserts the count text. |
| Docs site | `docs/src/content/docs/studio.md:60` describes the cell as "a lazy `GET /status` showing vertex/edge counts" - now false in two ways (the source and the scope). Rewritten in this feature. |
| Screenshots | `docs/src/assets/images/screen-connect.png` shows the table, so it is recaptured (standing rule: a UI change recaptures its images). |
| NL-assist dataset / eval, stored queries, recipes | **None.** No prompt, schema or persisted artifact mentions this cell. |
| Architecture diagrams | **None.** No new channel, deployable or layer. |

## 6. Testing

New `tests/instance-health.test.tsx` for the cell and `tests/namespace-totals.test.ts` for the pure
module:

- summation over a mixed inventory, grouping applied;
- one `notLoaded` entry gives `>=` on both counts, an exact `N`, and a tooltip naming the count;
- every entry `notLoaded` gives `-` on both counts, no `0` and no `>=`;
- an empty instance (one namespace, zero elements) gives `1 ns · 0 v · 0 e` (a real zero still reads
  as zero);
- probe unreachable gives `unreachable`, and no inventory request is made;
- probe unauthorized gives the existing wording, and no inventory request is made (the assertion
  that the row cannot 401 by construction);
- inventory 404 gives the probe's counts with no `ns` segment;
- inventory 500 gives `default:`-labelled probe counts.

## 7. Non-goals

- No new or changed REST surface, and no server-side aggregate.
- No change to any namespace-scoped display (chip, tiles, first-run gate, Namespaces panel).
- No per-namespace breakdown in the row: the Namespaces panel is that, one screen-section away.
- No new polling cadence: the inventory query reuses the existing 15s namespaces cache row.
