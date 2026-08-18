# Namespace startup default - Specification

> **Status:** SUPERSEDED (2026-08-18), never implemented. Nothing from this spec was built.
> [writable-instance-config](../../open/writable-instance-config/) absorbed it: once every configuration key
> becomes writable through a persisted overrides layer, `Fallen8:Namespaces:LoadOnStartup` is just
> one more restart-tier key, so this spec's catalog document slot (4.1), its `PATCH /ns` route (4.3),
> its fourth precedence level (4.2), its volatile-`409` (4.7) and its catalog re-stamp rule and test
> (4.8) are all unnecessary. What survived, as a phase of that feature: publishing
> `loadOnStartupDefault` and `startupLoadMode` on `GET /ns` uncomposed (4.4, 4.5) so `inherit`
> resolves in the Studio table, and deleting the fine print (5.1). This file is kept as the
> historical record and is not rewritten; see the successor's section 4.2 for the three behavioural
> deltas the supersession introduces. Precedent for recording abandoned-but-specified work:
> [multi-instance-host](../multi-instance-host/).

## 1. Why

F8 Studio's Namespaces table offers `load` / `skip` / `inherit` per namespace, and **nothing on the
screen says what `inherit` resolves to**. The owner's report: "It is completely not clear where the
'inherit' comes from. in the fine-print under the picture there is a note but nowhere else."

That is not a labelling oversight, it is a missing fact on the wire:

- `GET /ns` returns `{namespaces, maxNamespaces}` (`Controllers/NamespacesController.cs:73-86`).
- `GET /config` returns `{semantic, observability, apiKeyRequired}` and is the read-only
  instance-config home (feature [instance-config](../../done/instance-config/)).
- The value itself is latched into a **private** field, `_loadOnStartupDefault`
  (`Namespaces/Fallen8Namespaces.cs:130`), and reaches no response body anywhere.

So the Studio hint at `NamespacesPanel.tsx:421-426` names the configuration key in prose because it
structurally **cannot state its value**. The fine print is a symptom, not the disease: no wording
can resolve `inherit` for a client that is never told the answer.

Two further defects fall out of the same investigation:

1. **All three labels can lie, not just `inherit`.** `IsSelectedForStartupLoad` returns
   unconditional `true` for `StartupLoadMode=All` and unconditional `false` for `DefaultOnly`
   (`Fallen8Namespaces.cs:271-277`), short-circuiting the per-namespace policy. On a server booted
   with `All`, a row set to `skip` renders "skip" and is loaded anyway. Publishing only the
   inherited default would leave that intact.
2. **The key is documented but unreachable in the product.** `Fallen8:Namespaces:LoadOnStartup` is
   on the docs site (`namespaces.mdx:139`, `running.mdx:253`), and an operator can only act on it by
   editing configuration and restarting. The owner asked for it where the rest of the instance's
   posture already lives: "make the default behaviour configurable in the configuration section".

## 2. What this feature is, in one sentence

Give the Fallen-8 a **persisted, runtime-editable instance-level startup-load default**, publish it
(with the startup-load mode) on `GET /ns` so `inherit` resolves for every client, and delete the
fine print whose only job was to apologise for its absence.

## 3. Decisions the owner took (2026-08-18)

| Question | Decision | Rejected, and why |
|---|---|---|
| Read-only display, or a writable control in the Configuration panel? | **Writable.** The panel gains its first editor, persisted server-side. | Read-only value plus env key: cheaper and reopens no rule, but it answers "where does inherit come from" while leaving "and I still cannot change it here" - which is the half the owner asked for by name. |
| What does "the default should be not to load the namespace" mean? | **Leave the shipped fallback at `true`; make the default settable instead.** An operator who wants skip-by-default now sets it in the product, in one click, with no redeploy. | Flipping `appsettings.json` plus the C# initializer: breaking for every existing deployment (all `loadOnStartupEnabled: null` entries go `notLoaded` on the next boot), fails nine `NamespaceDurabilityTest` methods - six of which exist to prove data returns after a restart WITHOUT operator action - and makes a stock instance emit the `loaded == 0` Warning that exists to flag "the shape an operator gets wrong". Seeding new namespaces with an explicit `skip`: same nine failures, plus it makes create-fill-restart-503 the normal outcome of the create verb, and splits the inventory into two cohorts. |
| How much of the fine print survives? | **None of it.** The paragraph and its `namespace-startup-hint` testid go. | Shrinking it: still a paragraph of prose restating what the controls now show. |

### 3.1 This extends instance-config's rule, and says so

[instance-config](../../done/instance-config/spec.md) D6 (`spec.md:40`) reads "**Instance server
config is read-only + guidance** in the UI... No `PATCH /config`", and its non-goals (`spec.md:46-47`)
say "**No runtime config mutation** (`PATCH /config`); all `Fallen8:*` stays startup-only. Revisit
only [on a concrete operator need for live reconfiguration]".

This feature **invokes that revisit clause**; it does not pretend to be compatible with the
non-goal. What is and is not reopened, precisely:

- D6's **letter** is honoured: there is still no `PATCH /config`, and `GET /config` is untouched.
  The write lands on `PATCH /ns`, because the thing being set is namespace policy, not observability
  or provider configuration.
- D6's **spirit** is extended: the Configuration panel stops being purely read-only. Its subtitle,
  `docs/src/content/docs/studio.md:62` and any other published "read-only" sentence that becomes
  false must change **in the same PR**. This rule is enforced by no test, only by published prose,
  so an unnoticed violation would be pure doc-versus-code drift.
- What is **not** reopened: `Fallen8:*` keys stay startup-bound. The new setting is not a mutable
  configuration key - it is persisted state that a startup-bound key provides the fallback for,
  exactly as `pluginRegistrationEnabled` already relates to
  `Fallen8:Security:EnableDynamicPluginLoading`.

A writable **per-namespace** policy already ships (`PATCH /ns/{name}`, persisted in
`namespaces.json`). It is the **instance** default's writability that touches the rule, not "a
writable setting" as such.

## 4. The contract

| # | Rule |
|---|------|
| 4.1 | **Storage:** `loadOnStartupDefault` (`Boolean?`) on `NamespaceCatalogDocument`. `null` means "no instance default set - fall back to configuration". This is the **existing** document-level pattern, not a new one: `defaultPluginRegistrationEnabled` already occupies that slot shape (`NamespaceCatalog.cs:52-63`). |
| 4.2 | **Precedence, four levels, one home:** `Fallen8:Namespaces:StartupLoadMode` (`All` / `DefaultOnly` short-circuit everything) beats the namespace's own `loadOnStartupEnabled`, which beats the persisted `loadOnStartupDefault`, which beats `Fallen8:Namespaces:LoadOnStartup` (shipped `true`, **unchanged**). The one home is `IsSelectedForStartupLoad`; the docs page mirrors it as a table and nothing else restates it. |
| 4.3 | **Write:** `PATCH /ns` with body `{"loadOnStartupDefault": "enabled" or "disabled" or "inherit"}` - the same tri-state vocabulary `PATCH /ns/{name}` already uses for `loadOnStartup`, so no second vocabulary appears. `"inherit"` clears the persisted default so the configuration key applies again. Returns the updated `NamespacesREST`, so one round trip yields the recomposed state of every row. |
| 4.4 | **Read:** `NamespacesREST` gains `loadOnStartupDefault` (`Boolean`, always present) and `startupLoadMode` (`String`: `"catalog"`, `"all"` or `"defaultOnly"`). |
| 4.5 | `loadOnStartupDefault` on the wire is what a namespace with **no** override inherits **in `Catalog` mode** - i.e. the persisted slot, else the configuration key. It is deliberately **not** composed with the mode: composing them into one boolean would make the editor's own value fail to round-trip (under `All` it would always read `true`, so saving `skip` would appear to do nothing). The mode is published separately and clients compose. |
| 4.6 | **It changes the next boot only.** Like the per-namespace policy, setting the default never loads or unloads anything in the running process. |
| 4.7 | **A volatile Fallen-8 refuses the write with `409`.** With no catalog on disk (`_catalogPath == null`, `Fallen8Namespaces.WriteCatalogUnlocked`) there is nothing to persist to, and a setting that silently forgets itself at the next boot is worse than a refusal. |
| 4.8 | **The catalog writer must re-stamp the slot.** `WriteCatalogUnlocked` rebuilds the whole document from scratch on every write (`Fallen8Namespaces.cs:944-981`), so a document-level fact that is not both modelled on the DTO **and** held in memory is destroyed by the next create / rename / drop. This is a delayed-fuse bug - the setting appears to work and reverts minutes later - and it gets its own test, not just a line of code. |
| 4.9 | The reserved `default` namespace is unaffected: it is constructed with a fixed `LoadOnStartupEnabled = true` and `PATCH /ns/{name}` of the field answers `409`. It has no `inherit` state to resolve and gains no annotation. |

## 5. What the Studio renders

The two panels do **different jobs** and each states its fact once:

- **Configuration panel** owns the *control*: one row, `at startup (default)`, a writable select
  (`load` / `skip` / `inherit`), with `inherit` meaning "follow this server's configuration". This
  is the instance-level home, which is why the mode override is explained here and nowhere else.
- **Namespaces table** owns the *resolution*: the third option renders `inherit (load)` /
  `inherit (skip)`, reusing the other two options' own words rather than inventing a fourth
  vocabulary beside `STARTUP_OPTIONS`, `STARTUP_EFFECT` and the (deleted) hint.

What each mode renders:

| `startupLoadMode` | Namespaces table | Configuration panel |
|---|---|---|
| `catalog` | `inherit (load)` or `inherit (skip)` per the published default | writable select; value is the persisted default, or the configuration fallback when unset |
| `all` | each row's option set reads its policy unchanged | value reads `every namespace is loaded`; select **disabled** with the reason `Fallen8:Namespaces:StartupLoadMode=all overrides every policy` |
| `defaultOnly` | as above | value reads `nothing but "default" is loaded`; select disabled with the matching reason |

The mode is an instance-wide fact, so it is stated **once**, in the Configuration panel. The
Namespaces table does not repeat it per row.

On an older server that does not publish the two fields, the option renders bare `inherit` and the
Configuration row is absent. No client-side guess at the default is made.

### 5.1 The fine print goes, and where its two facts land

`NamespacesPanel.tsx:421-426` is deleted, `namespace-startup-hint` included. Neither surviving fact
becomes homeless:

- *"Takes effect on restart; nothing is loaded or unloaded in the running process."* Already stated
  by the mutation message (`STARTUP_EFFECT` plus "takes effect on restart",
  `NamespacesPanel.tsx:70-74,148`), by the select's `title`, and now by the Configuration row.
- *"A namespace that was not loaded reports no counts and answers 503 on every route but
  /status."* Already **rendered as state** in the row itself (the not-ready glyph and the dashed
  counts, `NamespacesPanel.tsx:231-248`), explained in full where the user actually lands in that
  state (`src/app/NamespaceScope.tsx:135-153`), and on the docs page. A paragraph in a management
  table is the one place it was not needed.

### 5.2 `lockNamespace` (studio-embeddable)

The new control **must** be gated on `!lockNamespace`. `ConfigurationPanel` renders unconditionally
(`ConnectScreen.tsx:278`) while `NamespacesPanel` is gated (`:280`), and
`tests/mount-seam.test.tsx:167-169` already asserts the startup control's absence by testid prefix
with the comment "a future panel-less home for it must not resurface it here". **This feature is
that panel-less home.** The assertion is widened to the new testid rather than sidestepped by
choosing a prefix outside its sweep - an embed scoped to one graph must not re-plan its host's boot
(`docs/src/content/docs/studio.md:12`, `embed-studio.md:68`).

## 6. What does not change

- **No engine change.** Startup-load selection lives entirely in the apiApp; the boot loop **is**
  the `Fallen8Namespaces` constructor (`Fallen8Namespaces.cs:192-259`), forced to construct before
  the host starts (`Program.cs:627-631`). Nothing gated on `HostCapabilities.SupportsBackgroundWork`
  is touched, so `tools/browser-probe` is not a required gate for this feature - stated here so the
  omission is a decision rather than a gap.
- **No shipped-default flip.** `appsettings.json` keeps `"LoadOnStartup": true` and the C#
  initializer keeps `= true`. Both would have to change together in any future flip: editing only
  `Fallen8NamespacesOptions.cs:60` is a **false green**, because `appsettings.json` is published into
  the image (`Dockerfile:35`) and is what every `WebApplicationFactory` test loads.
- **No `PATCH /config`**, and `GET /config` gains no field.
- **No new docs page.** `namespaces.mdx#startup-load` stays the single home and the heading is not
  renamed - eight or more pages link that fragment and the docs build is link-checked.

## 7. Failure modes, stated honestly

1. **4.8's delayed fuse.** Forget the re-stamp and the setting works, then silently reverts at the
   next namespace create, rename or drop. It is the highest-value test in the feature.
2. **A non-essential preference now lives in the one document whose corruption aborts boot.**
   `LoadCatalog` throws `InvalidOperationException` and takes the process down on unreadable or
   invalid JSON (`Fallen8Namespaces.cs:893-940`). The slot adds one nullable boolean to a document
   that already carries `defaultPluginRegistrationEnabled` under the same risk, so this widens an
   accepted exposure rather than creating one - but it is an accepted exposure, not a free one.
3. **A fourth precedence level is genuinely more to explain.** Mitigated by 4.2's single home and by
   reusing the existing tri-state vocabulary, not by hoping nobody notices.
4. **Setting the default to `skip` is still a loaded gun, just an aimed one.** Every namespace on
   `inherit` goes `notLoaded` at the next boot. Nothing on disk is lost (no engine means no
   write-ahead log is opened, and the shutdown save explicitly skips a not-loaded namespace rather
   than registering an empty checkpoint as its newest), and recovery is `POST /ns/{name}/activate`
   per namespace, but the operator now does this to themselves deliberately in one click. The
   control's own copy has to say so.
5. **One case cannot self-heal.** A skipped namespace whose directory holds checkpoint files no
   registry entry covers makes activation refuse with `409 UnregisteredCheckpoints` rather than
   publish an empty graph (save-games FR-11); the way out is `PATCH` plus restart plus `PUT /load`.

## 8. Impact on existing features (mandatory sweep)

| Feature / layer | Impact | Action |
|---|---|---|
| Engine (`fallen-8-core`) | **None** (section 6) | No change; `browser-probe` not required, reason recorded |
| [namespace-startup-load](../../done/namespace-startup-load/) | Completes it: the tri-state it shipped becomes resolvable, and its `NamespaceCatalog.cs:98-110` XML doc ("there is deliberately NO document-level slot") is superseded for the *instance default* while staying correct for the reserved `default` namespace | Rewrite that XML doc precisely; do not rewrite the historical spec |
| [instance-config](../../done/instance-config/) | D6's spirit extended under its own revisit clause (section 3.1) | Amend the panel subtitle and every published "read-only" sentence that becomes false, same PR |
| [graph-namespaces](../../done/graph-namespaces/) | Its LIVING `README.md:100-110` restates the inherit story | Amend in place |
| [plugin-registration](../../done/plugin-registration/) | Precedent donor only; its document slot is the pattern being reused | None |
| [studio-embeddable](../../done/studio-embeddable/) | The writable control lands in the one panel `lockNamespace` does not remove (section 5.2) | Gate on `!lockNamespace`, widen `mount-seam.test.tsx:167-169` |
| REST / [api-error-contract](../../done/api-error-contract/) | One new route (`PATCH /ns`), two new response fields, one new `409` (4.7) | Route plus fields; the `409` uses the existing problem+json home |
| OpenAPI snapshot | A **new path** trips `OpenApiDocumentTest`, `McpContractTest` and `McpRestCoverageTest`; the two new fields trip nothing, so `features/done/web-ui/openapi-v0.1.json` would go stale silently on fields alone | `powershell -File scripts/update-openapi-snapshot.ps1`, review the printed diff |
| MCP (engine to REST to MCP) | `PATCH /ns` is a new path, so the coverage gate **does** force a decision. Beyond the gate: `AdminTool.cs:75-76` already tells agents activation "does not change the startup policy", a dead end an agent cannot escape, because `f8_namespace`'s PATCH is rename-only | Bridge `PATCH /ns` and the per-namespace `loadOnStartup` field; surface both new read fields in `OverviewTool` |
| F8 Studio | Section 5 | `ConfigurationPanel.tsx`, `NamespacesPanel.tsx`, `api/types.ts`, `api/endpoints.ts`, shared `useNamespaces` query so the config row costs no extra request |
| Studio state screen | `NamespaceScope.tsx:150-153` says "a namespace left on **skip** is not loaded again after one [restart]" - incomplete once `inherit` can resolve to skip, and it points at a panel that does not exist under `lockNamespace` | Reword to name inherit-resolving-to-skip |
| Docs site | `namespaces.mdx` (:70 field table, :138-140 precedence table, the response-shape block), `running.mdx:253` (the key becomes a fallback), `studio.md:60,62` (pins the labels in prose and calls the panel read-only), `mcp-server.md:100,109` (the bridged surface grows), `troubleshooting.md:189-221` (cause 2 gains a second cause) | Amend in place; no new page, no sidebar change, heading not renamed |
| Root `README.md` | Namespaces already has a Key features entry carrying the startup-load clause | Amend in place; no new entry owed |
| Architecture diagrams | **None** - no new channel, no new deployable | Verdict recorded so the mandatory freshness check is visibly answered |
| Screenshots | `screen-connect.png` shows the Namespaces table **and** the Configuration panel, all edits in frame; `screen-connect-observability.png` photographs the overlay over a dimmed Connect screen whose Configuration panel gains a row | Recapture **both**; the observability capture must run against an OTLP-configured app or its Push section renders "off" and overwrites a good image |
| NL-assist fine-tune | **None.** This is namespace administration and instance config, not the graph-query surface (path/subgraph fragments, stored queries, property search) the dataset encodes | No dataset change, no `RETRAIN-LOG.md` entry; verdict recorded rather than skipped |
| [save-games](../../done/save-games/) / durability | No format change. A restore into a not-loaded namespace already flips `loadOnStartupEnabled = true` on the server's own initiative, which stays the documented reason restored data cannot go invisible at the next boot | No code change |
| Tests | The effective default, the option labels and the panel's read-only-ness are pinned by **nothing** today, in either direction | Section 9 |

## 9. What must be pinned (the gap this feature closes twice)

No test reads the option labels (only `toHaveValue`), no test asserts the effective default over
HTTP, and the OpenAPI snapshot gate compares only method / path / tags. So the change must bring
its own pins:

- `loadOnStartupDefault` composition over HTTP: configuration `true` gives `true`; configuration
  `false` gives `false`; the persisted slot **beats** the configuration key; `"inherit"` clears the
  slot and the key applies again.
- `startupLoadMode` published for all three modes, and 4.5's non-composition (under `All` the
  published default still reflects the slot, not `true`).
- **4.8:** set the default, then create / rename / drop a namespace, then re-read - the default
  survives. Then restart the host and re-read.
- `409` on a volatile instance (4.7).
- Studio: the three label forms; the Configuration row's write path; the row **absent** under
  `lockNamespace` with the widened `mount-seam` sweep; and a negative assert that the deleted hint
  is gone.
- MCP: the new fields reach `f8_overview`; `PATCH /ns` reaches the admin tier.

## 10. Non-goals (with revisit triggers)

- **No shipped-default flip** (section 3). *Revisit trigger:* a release deliberately labelled
  breaking, with the `Fallen8__Namespaces__StartupLoadMode=All` recovery in its notes.
- **No creation-time seed** and no `PUT /ns/{name}?loadOnStartup=`. *Revisit trigger:* operators
  reporting create-fill-restart-503 after setting the default to skip.
- **No runtime-mutable `StartupLoadMode`.** It is the escape hatch *from* persisted state; making it
  persisted state defeats its purpose. It stays configuration-only, and is now merely *visible*.
- **No `PATCH /config`** - instance-config D6's letter stands.
- **No idle eviction and no unload-on-demand** (inherited from namespace-startup-load).
