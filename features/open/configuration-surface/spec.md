# Configuration surface

**Status:** implemented on `feature/configuration-surface` and through the council gate; still under
`features/open/` because it is not merged to `main` yet. Move this directory to `features/done/` when
it lands.

## 1. Problem

The Connect screen renders every catalogued setting this instance binds as ONE flat, height-capped
scroll list, inline, between the semantic-provider cards and the observability one-liner. That list is
**102 rows** today (16 raw config sections; 50 of the rows are never writable and exist only to state
a rule and a reason). An operator who opens Connect to check which instance they are pointed at is
handed the entire configuration inventory in a 12-row window, sorted by nothing they chose, with no
way to narrow it.

The user's words: "much too overwhelming".

Two smaller defects ride along:

- **The observability keys render twice on Connect.** `Fallen8:Observability:TracingSamplingRatio`,
  `StatisticsElementBudget` and `StatisticsTopN` appear as editable rows in the flat list AND as
  read-only rows in the separate Observability overlay, whose copy ends with "The statistics bounds
  beside them ARE editable, in the Settings list above."
- **`docs/src/content/docs/observability.mdx` states those three keys are "display-only: these keys
  are startup-bound, so there is nothing to write back".** They are `Restart`-tier and writable. The
  same page contradicts itself ten lines earlier.

## 2. Shape

Connect keeps a **compact configuration summary card**: the two provider cards, the restart banner,
the observability one-liner, the unsaved-changes state, Refresh/Discard, and one `Configure...`
button. That is the same affordance the Observability block already uses, so the pattern is not new.

`Configure...` opens the **configuration surface**: one large modal dialog (Radix `Dialog`, the
mechanism the Observability overlay already uses) holding

- a **section nav** down the left, grouped under five headings, each entry carrying its key count,
- a **search** box and a **filter** strip above it,
- the **selected section's rows** on the right, sub-grouped, one section at a time,
- a footer with the read-only explanation (when the server refuses writes), the write error, and Save.

The existing Observability overlay **folds in as one section** of that surface. Its own dialog and its
own `Configure...` button go away, and with them the duplication in the flat list.

Nothing about the REST contract, the engine, or the settings catalog changes. This is a Studio
reorganisation of a surface that already exists.

## 3. What stays exactly as it is

These are inherited invariants from `features/done/writable-instance-config/`, and the redesign must
not weaken any of them. Each is named because moving the rows into a dialog is exactly the change that
could break it silently.

| Invariant | Where it lives after the change |
|---|---|
| The draft holds only touched rows; a key mapped to `null` is a pending CLEAR. | `ConfigurationPanel`, unchanged. |
| The config poll is suspended while there are unsaved edits, so a refetch cannot replace a value under a half-typed field. | `ConfigurationPanel`, unchanged, and now load-bearing while the surface is CLOSED as well. |
| A save carries only the keys in the draft, and clears only those keys from it on success, so edits typed into other rows while the request was in flight survive. | `ConfigurationPanel`, unchanged. |
| Switching the active instance drops the draft. | `ConfigurationPanel`, plus a new `setOpen(false)`. |
| A blanked numeric field blocks Save and names itself in the title. | Moves to the surface footer, same logic. |
| An environment-locked row is read-only and names the `Fallen8__...` variable to remove. | `SettingRow`, unchanged. |
| A never-writable key publishes no control at all, only its rule and its reason. | `SettingRow`, unchanged. |
| Restart wording has ONE home (`src/lib/restartCopy.ts`). | Unchanged, imported by the card. |
| `lockInstances` gates the whole editable region; `lockNamespace` additionally gates the `Fallen8:Namespaces:` prefix. | Computed once in `ConfigurationPanel`, passed down as one predicate. |
| A write failure keeps the read surface rather than collapsing it. | Surface footer. |

### The one deliberate obsolescence

`writable-instance-config` spec section 5.8 put the settings list on Connect between the provider
cards and the observability line. That placement is what this feature replaces. The rest of section 5
is inherited verbatim.

## 4. Section taxonomy

The section id derives from the key's **second segment**, matching the server's own rule
(`Fallen8OptionsSections.SectionOf`). It is never a hard-coded key list: a list would silently miss
the next key added under a section. An ordered table maps raw config sections to nav entries, and
**any raw section not in the table falls into a trailing `other` entry**, so a future options class is
visible rather than invisible.

Ordering is neither alphabetical nor the server's declaration order: read/write sections come first in
rough tuning order, the two entirely-read-only reference sections last. **Within a section, the
server's order is preserved verbatim**, because the catalog authors related keys adjacently
(`EnsureVectorIndex` is followed by `VectorIndexId`) and an alphabetical re-sort would split those
pairs.

| Group | # | id | Label | Keys | Raw sections |
|---|---|---|---|---|---|
| Graph | 1 | `namespaces` | Namespaces | 3 | `Namespaces` |
| Graph | 2 | `durability` | Storage and durability | 6 | `Durability` 5 + `Metadata` 1 |
| Workloads | 3 | `changefeed` | Change feed | 5 | `ChangeFeed` |
| Workloads | 4 | `analytics` | Analytics | 3 | `Analytics` |
| Workloads | 5 | `bulkio` | Bulk import and export | 3 | `BulkIO` |
| Workloads | 6 | `ceilings` | Registration ceilings | 2 | `Plugins` 1 + `StoredQueries` 1 |
| Semantic | 7 | `embedding` | Embedding provider | 21 | `Embedding` |
| Semantic | 8 | `chat` | Chat and language model | 9 | `Chat` |
| Semantic | 9 | `ingestion` | Document pipeline | 29 | `Ingestion` 23 + `Nlp` 6 |
| Operations | 10 | `integrations` | Integrations runtime | 3 | `Integrations` |
| Operations | 11 | `observability` | Observability | 6 | `Observability` |
| Reference | 12 | `identity` | Fleet identity | 4 | `Identity` |
| Reference | 13 | `security` | Security | 8 | `Security` |
| Not grouped yet | 14 | `other` | Other | 0 | anything unmapped |

3 + 6 + 5 + 3 + 3 + 2 + 21 + 9 + 29 + 3 + 6 + 4 + 8 = **102**, which is exactly what the catalog
publishes.

Each entry carries a one-line blurb, and those blurbs are the only genuinely new explanation this
feature authors. They say what the section governs, never what an individual key means: key meaning
lives on the server's own catalog reason and on each feature's docs page, and a second copy here would
drift.

### The four merges, each with its reason

1. **`Metadata` into `durability`.** Both are on-disk path keys excluded by the same rule; the
   `Fallen8:Metadata:Directory` reason names the namespace inventory, the save-game registry and the
   overrides file. `running.mdx` already files them under one owner.
2. **`Nlp` into `ingestion`.** The catalog says so itself: `Fallen8:Nlp:Enabled` "gates no REST
   endpoint and grants no caller anything. It only tells the ingestion pipeline whether to call the
   sidecar." Docling is already a sub-group inside Ingestion; the NLP sidecar becomes a second one.
3. **`Plugins` + `StoredQueries` into `ceilings`.** Identical shape (one `Int`, `LiveForNewWork`,
   minimum 1), and the catalog comment on the stored-query ceiling is literally "New registrations
   only, for the same reason as the plugin ceiling".
4. **Nothing else.** `Namespaces` keeps its own entry although `MaxNamespaces` is a third registration
   ceiling: the live-tier cross-cut is served by a filter, not by a merge.

### Sub-groups inside a section

Derived from the **third segment** when a key has four, so a provider's own keys sit together:
`chat` gives 4 direct + Ollama 2 + Nahil 3; `embedding` gives 10 direct + Onnx 5 + LLamaSharp 1 +
Ollama 2 + Nahil 3; `ingestion` gives 17 direct + Docling 6 + NLP sidecar 6; `identity` gives Tenant 2
+ Instance 2; `durability` gives 5 direct + Metadata 1. The implicit "direct" group renders first.
`analytics`, `bulkio`, `changefeed`, `integrations`, `namespaces`, `ceilings` and `security` render
flat.

`observability` is the ONE hand-authored exception: its groups are Push (OTLP), Pull (Prometheus
scrape) and Statistics snapshot, which is not a third-segment grouping (`TracingSamplingRatio` has
three segments and belongs to Push). It earns the exception because those three group hints are the
only in-product explanation of what those keys are for, and the server's catalog is structurally
forbidden from carrying that meaning. Any observability key the hand-authored map does not name still
renders, in a trailing group, so a key added later cannot vanish.

### Two reasoned non-goals

- **No grouping of never-writable rows by rule.** A never-writable row's `reason` IS its content, and
  the reasons under one rule differ per key (13 distinct reasons under Embedding alone), so collapsing
  to one sentence per rule would delete information. The overwhelm is solved by 8 rows in a pane
  instead of 102 in a list, plus search and a writable-only filter.
- **No per-key documentation in Studio.** The catalog carries no description field, on purpose. The
  surface says so plainly when a search finds nothing, rather than implying it could match meaning.

## 5. Interaction contract

**Opening.** One button, `config-configure`, rendered only once `GET /config` has answered. While the
read is pending or failed there is no button at all, because a dialog listing nothing is worse than
the card's own checking or unavailable note, which is the whole explanation.

**Search** matches the key, the rule and the reason, case-insensitively, and never the value (a value
can be an endpoint or a path, and a value search would turn the box into a way to fish for them). A
non-empty query shows matches **across all sections** as a flat, section-labelled list, because a
search that silently searched only the selected section would be a trap. The nav shows per-section
match counts while a query is active.

**Filters** are `all` (default), `writable here`, the restart-pending chip's own wording, `not
writable`, `set here`, `from the environment`. `not writable` keys on the tier, never on an absent
value. A filter never hides rows silently: the pane always states "N of M settings" for what it is
showing.

**Neither control is offered when there is nothing to narrow.** On an instance that publishes no
settings inventory the search box and the filter strip are absent, because the only section with
anything to say there is Observability, which reads its values off the observability block rather
than off descriptors, and any narrowing could only subtract it. For the same reason that section keeps
its hand-authored layout under a filter whenever it has no descriptors of its own.

**Closing with unsaved edits keeps the draft.** Escape and a scrim click stay Radix defaults. The
card keeps showing the unsaved-changes marker and the poll stays suspended; reopening restores the
draft; Discard on the card is the one documented way to drop it. Preserving someone's work beats a
confirmation dialog, and the state is visible from Connect either way.

**The surface never calls `useConfig`.** The card owns the single subscription. Two observers on the
same query key would let react-query take the shortest refetch interval, and a card left polling would
replace a value under a half-typed field in the open surface, which is the exact behaviour the poll
suspension exists to prevent.

**Section, query and filter state lives under `Dialog.Content`**, which Radix unmounts on close, so it
resets without an effect.

## 6. Impact on existing features

| Area | Verdict |
|---|---|
| Engine, REST contract, OpenAPI snapshot | **No change.** No route, model or XML doc is touched. |
| MCP coverage (`McpRestCoverageTest`, `McpContractTest`) | **No change.** No REST operation added or removed. |
| Provider-descriptor snapshot | **No change.** |
| `tools/browser-probe` | **No change.** No engine or host-capability code is touched. |
| `nl-assist-finetune` dataset / eval | **No entry in `RETRAIN-LOG.md`.** The dataset targets REST request bodies, and none change. |
| Architecture diagrams (root `README.md`, `docs/.../architecture.md`) | **No change.** No new channel, deployable or layer; neither diagram mentions Connect or Configuration. |
| Root `README.md` "Key features" | **No change.** The configuration entry describes behaviour and already links the live page. |
| `features/done/instance-config/`, `features/done/writable-instance-config/` | **Not rewritten.** They are historical records. Section 3 of this spec restates what they still bind. |
| `docs/src/content/docs/configuration.md` | **Updated.** It is the living doc for this surface. |
| `docs/src/content/docs/studio.md` | **Updated.** It describes the Connect screen's panels and embeds two of the three affected screenshots. Its `#connect` heading must survive: `standalone-ui.mdx` links it, and the docs build link-checks. |
| `docs/src/content/docs/observability.mdx` | **Updated**, including the pre-existing wrong claim that the three writable observability keys are display-only. |
| `docs/src/content/docs/semantic-traversal.mdx`, `embed-studio.md` | **One-line placement edits.** |
| `docs/src/content/docs/nahil.md` | **No change.** The provider and residency cards stay on Connect. |
| Screenshots | **Three recaptured:** `screen-configuration.png`, `screen-connect.png`, `screen-connect-observability.png`. |
| `NamespacesPanel` copy | **Updated.** It points at "the Configuration panel" and is legible in two published images. |
| Studio embed seams (`lockInstances`, `lockNamespace`, `usePortalContainer`) | **Honoured, not changed.** The new dialog must pass the portal container, or every scoped style stops matching in an embed. |

## 7. Out of scope

No REST change. No restart button (a single-process self-hosted server has no supervisor contract to
restart into, and that decision is unchanged). No configuration history, diff or versioning. No
per-key documentation in Studio. No new route, no new icon-rail entry: the surface is reached from
Connect, which is where instance-scoped concerns already live.
