# Writable instance configuration - Specification

> **Status:** OPEN (specified 2026-08-18). Nothing is implemented yet.
> This feature **retires** [instance-config](../../done/instance-config/) decision D6 ("instance
> server config is read-only + guidance... No `PATCH /config`") by invoking that feature's own
> revisit clause, and it **supersedes**
> [namespace-startup-default](../../done/namespace-startup-default/), which was specified and moved
> to `done/` unimplemented.

## 1. Why

The Connect screen's Configuration panel is read-only, so an operator who wants to change anything
about their own instance edits configuration on the host and restarts. The owner's request:

> "it's about being able to configure all Fallen-8 instance related configs. So transitioning from
> a read-only config page to a full read-write configuration. if possible those config parameters
> should be taking effect immediately or the user needs to be signalled that a restart is required"

There is also a measured documentation defect underneath it. Roughly 95 configuration leaf keys are
bound across 16 options classes; `running.mdx`'s reference table lists 20, and the whole docs site
names about 37. **Most of this instance's configuration surface is documented nowhere**, and the
product itself has never shown a single one of its values.

## 2. The honest arithmetic (read this before the contract)

Two facts constrain everything below, and both were verified rather than assumed.

### 2.1 Nothing is live today, and nothing becomes live for free

**Zero `Fallen8:*` sections are consumed through `IOptionsMonitor<T>` or `IOptionsSnapshot<T>.`** All
16 are plain `Services.Configure<T>` read through `IOptions<T>.Value`, which DI computes once and
caches for the process lifetime. The only `IOptionsMonitor` in the apiApp is the framework-mandated
`IOptionsMonitor<AuthenticationSchemeOptions>` at `Security/ApiKeyAuthenticationHandler.cs:53`, and
nothing subscribes to it.

`appsettings.json` already has `reloadOnChange: true`, and **nothing observes it**. So
`IConfigurationRoot.Reload()` on its own changes no behaviour whatsoever. Every live key costs a
named per-consumer conversion. "Applies immediately" is a consumer-plumbing feature, not a
configuration-source feature, and the spec says so up front rather than letting phase 4 discover it.

### 2.2 "All instance related configs" cannot be writable

Of the 74 keys classified so far: **0 live today, 34 live with named per-consumer work, 11
structurally restart-only, 29 must never be REST-writable.** The exact leaf total is deliberately
NOT asserted here - §5.1's governance test derives it, because a hand-authored count is how a
surface silently misses keys.

The never-writable third is not timidity. Representative cases, each verified:

- `Fallen8:Security:ApiKey` blank makes the handler authenticate nobody while the startup-installed
  `FallbackPolicy` still demands a principal: permanent 401 on every non-anonymous route, with **no
  REST recovery path**.
- `Fallen8:Durability:StorageDirectory` is not a write location but a **delete** location:
  `TryDrop` enumerates `fallen8.wal*` under a directory derived from it and then `Directory.Delete`s
  it (`Fallen8Namespaces.cs:767-782`).
- `Fallen8:Integrations:Endpoint` is the base address of the authenticated `/integrations/*`
  pass-through proxy, which forwards status, body and content type unchanged in both directions.
  Writable, it turns the apiApp into an authenticated arbitrary-URL proxy onto the operator's own
  network.
- `Fallen8:Embedding:ModelName` / `ModelVersion` / `Dimension` / `IntendedMetric` are the immutable
  identity stamp written beside every stored element embedding and into every bound vector index. A
  write is **silent corruption of already-stored vectors**, not a runtime error.
- `Fallen8:Observability:Prometheus:RequireApiKey` set false makes the boot call `AllowAnonymous()`
  on `/metrics`: literally an authenticated caller turning authentication off.

So the deliverable is **every key visible with its tier and the reason for it, and the writable
subset editable** - not "all configs writable". That framing is the feature, not a compromise on it.

## 3. Decisions the owner took (2026-08-18)

| Question | Decision |
|---|---|
| Where does v1 stop? | **Everything, including the live tier.** The full feature: catalog, overrides layer, read surface, write route, pending-restart signal, the live-tier consumer conversions, the Studio editor, MCP. |
| The narrow `namespace-startup-default` spec? | **Absorbed entirely.** Its catalog slot, `PATCH /ns` route, fourth precedence level and volatile-409 are deleted before being written (§4.2). |
| Writes on an instance with no API key? | **Never.** Two independent operator acts are required: configure a key AND enable the capability (§5.5.2). |
| Agents over MCP? | **Bridged**, admin tier, doubly gated (§7). |

## 4. What this retires and supersedes

### 4.1 instance-config D6

`features/done/instance-config/spec.md:40` and `:47` are retired by invoking their own revisit
clause ("Revisit only on a concrete operator need for live reconfiguration") and naming the need:
this request. Two framing rules for anyone reading that record:

- **D6 was a right-sizing scope cut, not a safety argument.** Git history shows D6 and the
  "No runtime config mutation" non-goal byte-identical to the original spec commit, and that spec's
  Security and privacy section never mentions mutability. So this feature must not argue down a
  safety objection instance-config never made.
- **Equally, there is no safety analysis to inherit.** That is exactly why §5.7's never-writable set
  is written from first principles and pinned by a test rather than by prose.

instance-config's specs and plans are historical records and are not rewritten; the retirement lands
in its status line and here.

### 4.2 namespace-startup-default, superseded

`Fallen8:Namespaces:LoadOnStartup` is structurally a next-boot-only key: `_loadOnStartupDefault` is
read only inside `IsSelectedForStartupLoad`, whose single call site is the boot loop in the
`Fallen8Namespaces` constructor. So **a persisted overrides file that the next boot reads is
behaviourally identical to the catalog document slot that spec designed** - both are "persisted,
applies at the next boot", which its own rule 4.6 already said.

**Dies before being written:** the `NamespaceCatalogDocument` slot (its 4.1); the `PATCH /ns` route
(its 4.3); the fourth precedence level (its 4.2), so `IsSelectedForStartupLoad` needs no edit at all;
the catalog re-stamp delayed fuse and its highest-value test (its 4.8); and the failure mode of a
non-essential preference living in the one document whose corruption aborts boot.

**Survives, as a phase:** `NamespacesREST` gains `loadOnStartupDefault` and `startupLoadMode`,
published **uncomposed** so the editor's value round-trips (composing them would make the published
default read `true` under `StartupLoadMode=All`, so saving `skip` would appear to do nothing); and
the Studio fine print is deleted. The read fields also fix the defect that predecessor found beyond
the labelling complaint: `All` / `DefaultOnly` short-circuit the per-namespace policy, so today a row
set to `skip` renders "skip" and is loaded anyway.

**Three deltas recorded rather than silently inherited:**

1. Its volatile-`409` (4.7) **disappears**. The overrides file resolves independently of
   `Fallen8:Durability:Volatile`, unlike `_catalogPath` which is only assigned inside
   `if (!_durability.Volatile)`. A volatile instance can persist this setting. Deliberate reversal:
   the setting is operator configuration, not graph data, and its honest argument for the refusal
   (a setting that silently forgets itself) no longer applies.
2. Its `lockNamespace` gating does not carry over unchanged: the editor gates on `!lockInstances`
   (instance-wide configuration belongs to the host), so the namespace-startup row needs an
   **additional** `!lockNamespace` gate, and `mount-seam.test.tsx:167-169`'s testid sweep - whose
   comment already says "a future panel-less home for it must not resurface it here" - is still
   widened.
3. Its "bridge `PATCH /ns`" action disappears with the route. `AdminTool.cs:75-76`'s dead end
   (activation "does not change the startup policy", inescapable because `f8_namespace`'s PATCH is
   rename-only) is answered by bridging the per-namespace `loadOnStartup` field instead.

**One behavioural delta that is NOT equivalent:** the predecessor's slot deliberately outranked the
configuration key. An overrides layer placed below the environment cannot. On an instance that sets
`Fallen8__Namespaces__LoadOnStartup` by environment - which `running.mdx:253` actively invites - the
instance default is env-locked and the control says so (§5.3). The shipped compose does not set it,
so `env:up` instances are unaffected.

## 5. The contract

### 5.1 The setting catalog is the one home

`Configuration/Fallen8SettingCatalog.cs` carries one entry per configuration leaf key:
`Key`, `Kind` (`bool` / `int` / `double` / `string` / `enum`), `Bounds`, `AllowedValues`, `Tier`,
`ApplyMode`, and - for writable-live entries - a non-null `ApplyNow` delegate.

| Rule | |
|---|---|
| 5.1.1 | **A governance test derives completeness**, reflecting over every options class and failing unless every public leaf property is catalogued with a tier, or listed `NotWritable` **with a reason**. It also asserts `live + restart + notWritable == the reflected leaf count`, so the totals are derived and never arithmetic. This is the same shape as the enforced MCP bridge-or-defer rule: a future option property forces a recorded decision. |
| 5.1.2 | **A `Live` entry with a null `ApplyNow` fails the suite.** "Declared live" is mechanically checkable, so it can never be aspirational. |
| 5.1.3 | **The catalog carries no human prose.** Tier, bounds, allowed values and apply mode only. `GenerateDocumentationFile` is already on and the OpenAPI pipeline already consumes the options classes' XML docs - that is where a key's meaning lives, and a second copy in the catalog would be exactly the multi-home duplication this repo forbids. The UI links a key's owner doc; it does not restate it. |
| 5.1.4 | **`AllowedValues` is load-bearing, not decorative.** `Fallen8:Chat:Backend` is a free-form `String` that `ChatBackendFactory` switches on by exact ordinal match and throws otherwise; the throw is cached by a `Lazy<IChatBackend>` and surfaces as a permanent 503. There is no `IValidateOptions` anywhere in the apiApp and binding a `String` never fails, so trial-binding cannot catch `"ollama"`. The catalog's allowed-value set is what turns that into a `400`. |
| 5.1.5 | **The served descriptor JSON is snapshot-pinned** with a regenerate script, following `ProviderDescriptorSnapshotTest`. That gate exists because a published screenshot once showed settings the runtime does not offer; a UI rendered from a hand-authored catalog reproduces that risk exactly. 5.1.1 proves completeness, not stability. |

### 5.2 Three tiers and three apply modes

**Tiers:** `Live` (writable, takes effect in this process), `Restart` (writable, takes effect at the
next boot), `NotWritable` (§5.7).

**Apply modes** are what the UI promises, and there are three because the runtime has three:

| `applyMode` | Meaning |
|---|---|
| `live` | Takes effect for everything, immediately. |
| `liveForNewWork` | Takes effect for **new** work only, immediately. Required for honesty: `MaxSubscribers` / `SubscriberQueueSize` are read inside `TrySubscribe`, and `Plugins:MaxCount` / `StoredQueries:MaxCount` compare at registration and **never evict** (documented on `PluginRegistry`). Lowering `MaxSubscribers` from 40 live subscribers changes nothing for them, so reporting plain "applied" would be the silently-did-not-apply class this feature exists to eliminate. The copy names what is unaffected. |
| `restart` | Persisted now, applies at the next boot. |

### 5.3 The overrides layer: arbitration, not ordering

`Fallen8ConfigOverridesSource` persists `config.overrides.json` in `Fallen8:Metadata:Directory`,
beside `savegames.json` and `namespaces.json`, and is a **real configuration source** so
`Restart`-tier writes genuinely apply at the next boot with no extra machinery.

| Rule | |
|---|---|
| 5.3.1 | **It outranks `appsettings.json` and `appsettings.{Environment}.json`, and never outranks user secrets, environment variables or the command line.** It must beat appsettings because that file ships 26 keys at their code defaults, covering roughly 14 of the writable set, so a layer underneath would be dead on most of the feature. It must not beat the environment because the shipped compose declares 26 `Fallen8__` keys plus one baked into the image, and the docs actively instruct operators to set `Fallen8__` variables by hand for the keys with no `F8_*` mapping. |
| 5.3.2 | **Mechanism is per-key arbitration, not source ordering.** The source is appended last (so it beats appsettings), but the provider emits a key only when no environment-variable or command-line provider **declares** it. Authority is probed directly by keeping references to those providers and `TryGet`ing the bounded catalog key set - never inferred. This handles compose's `${VAR:-}` idiom correctly by construction: `Fallen8__Security__ApiKey` is always declared as the empty string, and a declared empty string is still a declaration, so "unset" is never used as a proxy for "the operator has no opinion". |
| 5.3.3 | **The provider no-ops unless its path is explicitly resolved from configured metadata.** It must NEVER fall back to `AppContext.BaseDirectory`. In the test suite that directory is the shared test output directory used by 52 `WebApplicationFactory` files, several of which set catalog keys through `UseSetting`; an appended-last overrides file would outrank those and poison the whole run **and every later run**. The same fallback would silently eat a dev operator's saved configuration under `bin/Debug/net10.0/metadata`. Write-path tests point it at a per-test temp directory. |
| 5.3.4 | **`reloadOnChange` is deliberately `false` on this source.** The write path reloads explicitly; a file watcher would make the pending-restart derivation race its own writes. |
| 5.3.5 | **Neither a silent no-op nor a silent override**, enforced at three points: **write time**, `PATCH` answers `409` and writes NOTHING when any key in the batch is environment- or command-line-declared, naming each refused key, its authority and the exact `Fallen8__…` form (no force flag, and the value is not stored-but-shadowed, which would be a time bomb that arms the day the variable is removed); **read time**, every descriptor carries `source` (`default` / `appsettings` / `userSecrets` / `environment` / `commandLine` / `override`), resolved by walking providers in reverse; **boot**, one log line per overridden key the environment outranks. |
| 5.3.6 | **No writable key is an array.** Configuration arrays merge index-wise across providers, so an override could overwrite index 0 but never shrink or clear a longer environment-provided list. Arrays are dodged, not solved, and this is stated rather than discovered. |

### 5.4 `GET /config` grows the read surface

`ConfigREST` gains `settings[]` (one descriptor per catalogued key: `key`, `kind`, `tier`,
`applyMode`, `value`, `source`, `restartPending`, `bounds`, `allowedValues`, `reason`) and
`pendingRestart[]`.

| Rule | |
|---|---|
| 5.4.1 | **A `NotWritable` key publishes no value.** It emits key, tier, source, reason and `valueWithheld: true`. `GET /config` carries neither `[Authorize]` nor `[AllowAnonymous]` and the `FallbackPolicy` is installed only when a key is configured, so on a keyless instance the route is **anonymous** - and today's body is a hand-written allowlist that deliberately omits sidecar URLs, model file paths and durability paths. Publishing all values would emit `Nlp:Endpoint`, `Integrations:Endpoint`, `Embedding:Onnx:ModelPath` and `Durability:WalPath` to any unauthenticated caller, caught by no existing gate. A test asserts no never-writable key's value appears in the body. |
| 5.4.2 | Visibility is the half that closes the documentation gap of §1, and it is independently valuable: it ships before the write route works and it is what finally answers "what does `inherit` resolve to". |

### 5.5 `PATCH /config` writes

#### 5.5.1 Semantics

A new **method on the existing `/config` path**, never a new path: `/config` is already in the
hard-coded Fallen-8-level set that the path-twinning gate checks, so a new method passes free while
`/config/settings` would fail until listed. The action carries `[Fallen8Level]`, or the namespace
route convention emits a semantically wrong `/ns/{ns}/config` twin - which is also a new path in the
snapshot.

Order of operations: validate **every** key in the batch (catalogued, writable, in bounds, in the
allowed-value set) and refuse the whole batch before mutating anything, following the
`PATCH /ns/{name}` precedent; trial-bind against a throwaway configuration root; write durably once;
`Reload()`; run the `ApplyNow` delegates for `Live` entries; return the **effective** value read back
off the freshly bound options, so a coerced value is visible rather than assumed. A `null` value
clears the override and restores the layer below - that is the undo, and no versioning is needed.

A `Restart`-tier write returns `200` and persists. It is never a `202`, never an error, and never a
silent no-op.

#### 5.5.2 Authorization: two independent operator acts

A new `Fallen8:Security:EnableConfigurationWrite` (default `false`, itself absent from the catalog so
the write surface can neither disable nor re-enable itself), plus a capability policy, plus:

> **No API key configured means no configuration write, ever - even with the capability enabled.**

The four existing capability policies add `RequireAuthenticatedUser` only when a key is configured,
so a symmetric policy would make `PATCH /config` **anonymously writable on the default deployment**.
Unlike the existing anonymous code execution, which is per-request and non-persistent, a
configuration write persists a posture change that survives restart. The panel explains the
requirement instead of showing a dead Save button.

Consequence to accept: the shipped `env:up` compose instance is keyless
(`Fallen8__Security__ApiKey=${F8_API_KEY:-}`, and `env-info.js` treats a set `F8_API_KEY` as an
anomaly to warn about), so **the docs screenshot recipe must set `F8_API_KEY`** or it photographs a
read-only panel. Stated here because the two requirements collided in an earlier draft.

### 5.6 The pending-restart signal is derived, never stored

A `Fallen8ConfigOverrides` singleton captures a boot snapshot of the effective value of every
catalogued key, taken immediately after the eager `Fallen8Namespaces` construction - the real latch
moment for six sections at once. A key is **pending** when its tier is `Restart` and its currently
configured effective value differs from that snapshot.

There is deliberately **no marker file, no `appliedAtStartup` flag and no cleanup path**: the pending
set clears exactly when the process starts, because the reference value exists only in memory. It is
recomputed on every `GET`, so it survives a page reload, a tab close, a different browser and a
reconnect - the requirement that the pending state live on the server is met by construction rather
than by synchronising a client cache.

One nuance the copy must respect: because `appsettings.json` has `reloadOnChange: true` and nothing
observes it, the banner also lights up when an operator hand-edits that file. The wording is
therefore **"differs from the value this process started with"**, never "you changed this".

**No restart button and no restart endpoint.** A single-process self-hosted server has no supervisor
contract to restart into; the banner tells the operator what their own `docker compose restart` will
apply. *Revisit trigger:* a supervised deployment shape that defines what "restart" means.

### 5.7 The never-writable set, and the rules that generate it

Stated as **rules** so the list is derivable and a future key classifies itself:

| Rule | Excludes |
|---|---|
| R1 | **No key under `Fallen8:Security` is writable.** Easier to state, test and review than per-knob carve-outs, and it covers the lockout generators (`ApiKey`, `ApiKeyHeader`), the code-execution switch (`EnableDynamicPluginLoading`), the perimeter (`AllowedCorsOrigins`), the only brake on 23 sensitive actions (the two rate-limit keys), the benchmark bound, and the new capability gate itself. |
| R2 | **No key that addresses on-disk state.** `Durability:StorageDirectory` (a delete path), `WalPath` (orphans uncheckpointed commits), `CheckpointBaseName` (it is also the discovery glob), `Volatile` (selects the engine constructor), `Metadata:Directory` (locates the overrides file itself, so the layer cannot set the key that finds it). |
| R3 | **No key that is part of stored-data identity.** The embedding stamp (`ModelName`, `ModelVersion`, `Dimension`, `IntendedMetric`), everything that changes the embedding **function** under an unchanged stamp (`Backend`, `Ollama:Model`, `Onnx:MaxTokens`, `Onnx:Pooling`, `Onnx:Normalize`), and the index-identity keys `Ingestion:EmbeddingName`, `VectorIndexId`, `FulltextIndexId`, `EntityIndexId`. The last two carry `VectorIndexId`'s exact hazard: changing either orphans a populated index and makes search **silently empty**. Several of these are mechanically per-operation and *would* go live with a monitor, which is precisely the danger. |
| R4 | **No URL the server dials** (SSRF): the embedding, chat, Docling and NLP sidecar endpoints, `Observability:Otlp:Endpoint` (signal exfiltration of metrics, traces and logs), and `Integrations:Endpoint` (the sharpest case: an authenticated arbitrary-URL proxy onto the operator's own network). |
| R5 | **No capability flag.** `Embedding:Enabled`, `Chat:Enabled`, `Ingestion:Enabled`, `Integrations:Enabled` are the operator's 403 opt-out; lifting one is straightforward privilege escalation, and `Chat:Enabled` additionally rides the anonymous `/status` probe. `Observability:Prometheus:Enabled` and `Prometheus:RequireApiKey` move only together in configuration, because the latter defaults false and enabling the former alone opens an anonymous `/metrics`. |
| R6 | **No fleet-attribution key.** The `Fallen8:Identity:*` values are baked into the OpenTelemetry resource attributes at boot, so a write could only falsify the reported identity of a process whose telemetry already went out under the real one. |
| R7 | **No dead knob.** `Security:AllowRemoteAccess` is read by no product code (its own XML doc says "Reserved and currently NOT enforced"), and `Nlp:MaxBatchSize` is bound and documented but read by nothing, because the enrich path builds one batch from every chunk. Exposing either would advertise a control the app does not implement - the defect class that got a previous phantom limit deleted and permanently blocked by a test. Implement it or delete the property; never put it on a config surface. |

### 5.8 Monitors go in per key, never per provider

**Hard rule, with a test.** Converting a consumer to `IOptionsMonitor` makes *every* key on that
section live, including never-writable ones, because `appsettings.json`'s `reloadOnChange` is on in
production and off only in tests. `DurabilityLifecycleService` reads `Volatile` and `SaveOnShutdown`
in one expression and `CheckpointBaseName` a few lines later, so a wholesale
`_options = monitor.CurrentValue` would silently make two R2 keys live.

So: a converted consumer reads **the specific live key** from `CurrentValue` and every other key from
the boot snapshot. The same rule forbids handing the chat or embedding providers a whole-section
monitor: both expose `Backend` / `Model` / `Endpoint` through per-access properties feeding
`/status` and `/config`, while the things that do the work bake those values at construction. A
configuration view that truthfully reports a backend the process is not using is worse than an
honest "restart to apply".

The existing optional-`IOptions` plus `?? new T()` constructor pattern is preserved so
direct-construction tests keep working.

## 6. F8 Studio

The Configuration panel becomes a tiered editor and the codebase's **first dirty-state form** (there
is no `dirty` / `unsaved` / `hasChanges` / `beforeunload` precedent anywhere in `src/`).

| | |
|---|---|
| 6.1 | A generic `SettingRow` rendered from the descriptor's `kind`, using existing primitives only (`.input`, `.input w-auto` for selects, `.label`, `.btn`) - no new CSS and no new visual language. |
| 6.2 | **`useConfig`'s 10 s `refetchInterval` is suspended while the form is dirty**, or the poll stomps inputs mid-typing. `config-refresh` relabels to a discard-and-reload while dirty. |
| 6.3 | Per-row `source` badge; an environment-declared row renders **disabled** with the caption "set by `Fallen8__…` in the environment", reusing the env-key caption vocabulary the observability overlay already has. A row whose stored override wins gets a "set here" badge and a Clear action. |
| 6.4 | The pending-restart banner (`data-testid="config-pending-restart"`) is a **state** derived from the poll, not a toast, and discloses key / running value / pending value. |
| 6.5 | The editable region gates on **`!lockInstances`** (the panel renders ungated today, unlike `NamespacesPanel`); the namespace-startup row additionally on `!lockNamespace`. |
| 6.6 | A failed `PATCH` renders inline at `config-settings-error`; it must not collapse the panel into `config-unavailable`, which is today's all-or-nothing failure mode. New fields are read defensively (`settings ?? []`), as the panel already does for `semantic?.embedding`. |
| 6.7 | **One home for the restart phrasing.** `src/lib/restartCopy.ts` exports the chip label and the banner summary; `NamespacesPanel` imports it instead of borrowing the Configuration view's register by comment, which is what it does today. |
| 6.8 | The panel subtitle drops "read-only", and the observability overlay's "this view is read-only" description is rewritten - that overlay becomes the clearest demonstration of mixed tiers, since its three statistics and tracing numbers become writable while the Prometheus and OTLP keys stay read-only beside them. |
| 6.9 | The absorbed namespace phase: the `at startup` option renders `inherit (load)` / `inherit (skip)`, the `namespace-startup-hint` paragraph and testid are **deleted**, `NamespaceScope`'s prose is reworded to name inherit-resolving-to-skip, and `mount-seam.test.tsx`'s testid sweep is widened. |

## 7. MCP (engine to REST to MCP)

Bridged, per the owner's decision. `f8_admin` (already admin tier) gains `get_settings` and
`set_settings`, **doubly gated** on the server capability and `F8_MCP_ENABLE_ADMIN`.

The existing `GET /config` deferral entry must be **deleted**, not narrowed: the coverage gate's
disjointness assertion fails when a bridged endpoint also matches a deferral. Ordering is
load-bearing - regenerate the OpenAPI snapshot **before** adding either bridge entry, because the
contract test requires every bridged route to exist in the pinned snapshot.

Accepted consequence, stated rather than discovered: bridging the write lets an agent widen the very
limits that bound its own runs (analytics time budgets, ingestion ceilings, change-feed subscriber
caps). The double gate is the mitigation, and the never-writable set means it can never reach
authentication, storage paths or stored-data identity.

Separately, bridge the per-namespace `loadOnStartup` field on `f8_namespace` to close the dead end
`AdminTool` documents today: an agent can activate a namespace but never make it survive a restart.

## 8. Failure modes, stated honestly

1. **A wrong "applies immediately" claim is the worst defect this feature can ship**, because it
   fails silently. 5.1.2 and 5.2's `liveForNewWork` exist for that, and every live key gets a test
   asserting **observed behaviour** changed (a subscribe is refused, a create 422s) - never that the
   option value changed.
2. **Two "the message lies about the cap" traps** decide the tier boundary rather than being
   footnotes: `Analytics:MaxConcurrentRuns` is quoted in the 429 text while the real cap is an
   unresizable `SemaphoreSlim`, and `Ingestion:MaxQueueLength` is quoted in the 503 while the real
   cap is an immutable bounded `Channel`. Both stay `Restart` tier so both reads come from the same
   boot snapshot and the messages stay truthful.
3. **A latched embedding provider still needs a restart.** The metric latch, the dimension latch and
   the `Lazy`'s cached creation exception have no reset path, so `embedding-provider`'s published
   promise that a failed load "answers 503 until config changes" is satisfied today only by a
   restart. No reload is claimed to clear it.
4. **The overrides file is a new file in the metadata directory** on the same volume as the namespace
   inventory and the save-game registry. It holds no secret by construction (R1), which is also what
   keeps it off the secret-at-rest ledger.
5. **The read surface exposes how much was undocumented.** Roughly 58 keys become visible that no
   doc has ever described. That is the point, and it is also a support surface.

## 9. Impact on existing features (mandatory sweep)

| Feature / layer | Impact | Action |
|---|---|---|
| Engine (`fallen-8-core`) | **No contract change.** Two engine-side properties are fan-out targets and already settable, re-read at registration, and documented as never evicting: `PluginRegistry.MaxCount`, `StoredQueryLibrary.MaxCount`. `DurableFileIo.ReplaceAllTextDurably` is already public for this kind of reuse | No engine edit; verdict recorded |
| [instance-config](../../done/instance-config/) | D6 and its non-goal retired (§4.1) | Retire in writing; do not rewrite its historical spec |
| [namespace-startup-default](../../done/namespace-startup-default/) | Superseded unimplemented (§4.2) | Already moved to `done/` with a status line |
| [api-security-boundary](../../done/api-security-boundary/) | Its honest-limits paragraph says a key holder is trusted as the process, but not that a key holder may change the posture **other callers** see | **Extend** the declared single home on the security docs page; never restate it elsewhere |
| [embedding-provider](../../done/embedding-provider/) | Its "answers 503 until config changes" promise is restart-only in practice (§8.3) | State it plainly in this spec and on the page; no false reload claim |
| [observability](../../done/observability/) / semantic pages | Five published sentences become false | Amend in place |
| REST contract | One new method on an existing path; field additions to `ConfigREST` and `NamespacesREST`; new `409` (arbitration) and `403` (capability or no key) | `[Fallen8Level]` on the action; sensitive-rate-limit plus request-size attributes as every `/plugins/*` action uses; existing problem+json homes |
| OpenAPI snapshot | Trips twice, one **silently**: the new method trips the path/method inventory; the field additions trip **nothing**, so the pinned snapshot goes stale invisibly | Regenerate in both phases, review each diff, and **before** any MCP entry |
| MCP | Bridged (§7); the `GET /config` deferral is deleted | `McpBridgedEndpoints`, `f8_admin`, plus the `f8_namespace` field |
| F8 Studio | §6; first dirty-state form; panel is ungated today | Per §6, including the `!lockInstances` gate |
| Docs site | New `configuration.md` as the ONE home for the tier model, source resolution and the pending-restart signal, registered in the sidebar, plus a root README Key features line | **Keep** `running.mdx`'s 20-row table and the six per-feature key tables: operators configure by environment *before* the server and therefore the UI exists. `configuration.md` deliberately lists no keys - the panel is the live inventory |
| Screenshots | `screen-connect.png` bakes "this instance · read-only"; `screen-connect-observability.png` bakes "this view is read-only". Plus a new `screen-configuration.png`, since the editor is the feature's whole point | Recapture both, add the new one, and **set `F8_API_KEY`** in the recipe or the editor does not render (§5.5.2). The observability capture needs an OTLP-configured app or its Push section renders "off" |
| NL-assist dataset | **None.** There is no `Fallen8:*` NL-assist key: routing is a browser-only preference and server-side NL depends only on `Fallen8:Chat:*` | No dataset change, no `RETRAIN-LOG.md` entry. Also fix the stale `ConfigurationPanel` docstring that claims the NL preference lives there |
| Architecture diagrams | **None** - no new channel, no new deployable | Verdict recorded |
| `tools/browser-probe` | **Not implicated** - apiApp only; nothing gated on `HostCapabilities.SupportsBackgroundWork` | Verdict recorded; not a gate for this feature |
| Provider-descriptor snapshot | **None** - no integration descriptor change | Verdict recorded |
| Compose / env scripts / Dockerfile | The declared `Fallen8__` keys are the arbitration input set; no `F8_*` variable exists for any writable key | No compose change. Add a test parsing `docker-compose*.yml` and the Dockerfile for `Fallen8__` assignments, so a future compose edit that starts declaring a writable key fails loudly instead of silently deadening a UI field. Syntax-check any touched script with `node --check`; **never** run them (they invoke docker compose on require) |
| Sibling deployables | Unreachable: `fallen-8-mcp` and `fallen-8-integrations` read their own prefixes. A panel titled "this instance" implies otherwise | The docs page states the surface covers the apiApp process only |
| Tests | The panel's read-only-ness, the effective default and the option labels are pinned by **nothing** today; the security boundary test is hand-picked spot checks, not a route sweep, so a new write route gets **zero** automatic pipeline-auth coverage | §5's pins plus hand-written security cases; never read a green suite as enforced auth |

## 10. Non-goals (right-sized, with revisit triggers)

- **No restart endpoint and no restart button** (§5.6).
- **No writable arrays** (5.3.6). *Revisit trigger:* a writable key that is genuinely a list.
- **No `Analytics:MaxConcurrentRuns` live.** It needs the run gate rewritten from `SemaphoreSlim` to
  an interlocked counter - a correctness change to a concurrency primitive - plus the 429 message
  fixed in the same change. *Revisit trigger:* an operator asking to change analytics parallelism
  without a restart.
- **No `Observability:TracingSamplingRatio` live.** It needs a custom sampler, and it would only work
  where an OTLP endpoint existed at boot: live in one deployment and restart-required in another is a
  worse contract than restart-required everywhere.
- **No second live tranche in v1.** Nine ingestion ceilings, three NLP ceilings and the upload cap
  are deliberately held back; two of them change the chunk-boundary contract for new documents and
  leave a namespace with mixed-pipeline chunks, which deserves its own paragraph. *Revisit trigger:*
  an operator tuning ingestion on a live corpus.
- **No configuration history, no audit log, no diff view, no export/import.** Single-process
  self-hosted reality; the write is logged before-and-after in the server log. *Revisit trigger:* a
  deployment with more than one operator.
- **No `PATCH` of a key outside the catalog.** Unknown keys are `400`, never stored speculatively.
