# Writable instance configuration - Specification

> **Status:** OPEN (specified 2026-08-18, condensed 2026-08-19). Nothing is implemented yet.
> This feature **retires** [instance-config](../../done/instance-config/) decision D6 ("instance
> server config is read-only + guidance... No `PATCH /config`") by invoking that feature's own
> revisit clause and naming the operator need below, and it **supersedes**
> [namespace-startup-default](../../done/namespace-startup-default/), which was specified and moved
> to `done/` unimplemented. Vocabulary: this feature says **"live"** and **"restart-required"**;
> it never says "reconfigurable", which fleet-observability already spent on
> environment-plus-`env:up` while explicitly denying hot reload.

## 1. Why, and the two constraints

The Connect screen's Configuration panel is read-only; changing anything means editing host
configuration and restarting. The owner asked for a full read-write configuration surface where
values apply immediately or the operator is told a restart is required. Underneath sits a
documentation defect: **94 configuration leaf keys** are bound across 16 options classes (measured by
the phase-1 governance sweep, not estimated), the docs site names about 37, and the product has never
shown a single value.

Two verified facts constrain the contract:

1. **Nothing is live today, and nothing becomes live for free.** All 16 options classes are plain
   `Services.Configure<T>` read through `IOptions<T>.Value`, which DI computes once and caches for
   the process lifetime. `appsettings.json` already reloads on change and nothing observes it, so
   `Reload()` alone changes no behaviour. Every live key costs a named per-consumer conversion
   (§4.8): "applies immediately" is consumer plumbing, not a configuration-source feature.
2. **"All configs writable" is impossible.** **44 of the 94 keys, nearly half**, must never be
   REST-writable: lockout generators, on-disk delete paths, stored-data identity stamps, dialable
   URLs, capability flags (§4.7). The deliverable is therefore **every key visible with its tier
   and reason, and the writable subset editable** - that framing is the feature.

The worst defect this feature can ship is a wrong "applies immediately" claim, because it fails
silently. Rules 4.1.2, 4.2's `liveForNewWork` mode, and the behaviour-asserting test rule in the
plan all exist for that.

## 2. Owner decisions (2026-08-18)

| Question | Decision |
|---|---|
| Where does v1 stop? | **Everything, including the live tier**: catalog, overrides layer, read surface, write route, pending-restart signal, live-tier consumer conversions, Studio editor, MCP. |
| The narrow `namespace-startup-default` spec? | **Absorbed entirely** (§3.2). |
| Writes on an instance with no API key? | **Never.** Two independent operator acts required: configure a key AND enable the capability (§4.5). |
| Agents over MCP? | **Bridged**, admin tier, doubly gated (§6). |

## 3. Supersessions

### 3.1 instance-config D6

D6 and its "no runtime config mutation" non-goal are retired by their own revisit clause ("revisit
only on a concrete operator need for live reconfiguration"): this request is that need. D6 was a
right-sizing scope cut, not a safety argument, so there is no safety analysis to inherit - §4.7 is
written from first principles and pinned by tests. The historical spec is not rewritten; the
retirement is one line in its status header.

### 3.2 namespace-startup-default

`Fallen8:Namespaces:LoadOnStartup` is structurally next-boot-only (read once, inside the boot
loop's `IsSelectedForStartupLoad`), so a persisted overrides file the next boot reads is
behaviourally identical to the catalog-document slot that spec designed. **Deleted before being
written:** its `NamespaceCatalogDocument` slot, its `PATCH /ns` route, its fourth precedence level
(so `IsSelectedForStartupLoad` needs no edit), and its catalog re-stamp fuse. **Survives,
distributed across the plan's phases:** the two keys as restart-tier catalog entries (phase 1);
`NamespacesREST` gains `loadOnStartupDefault` and `startupLoadMode`, published **uncomposed** so
the editor round-trips (composed, `skip` would appear to do nothing under `StartupLoadMode=All`)
(phase 2); bridging per-namespace `loadOnStartup` on `f8_namespace`, closing the dead end
`AdminTool` documents (phase 3); the Studio row and the deletion of the `namespace-startup-hint`
fine print (phase 5).

Three recorded deltas: (1) its volatile-`409` disappears - the overrides file resolves
independently of `Durability:Volatile`, and the setting is operator configuration, not graph data;
(2) the editor gates on `!lockInstances`, the namespace-startup row **additionally** on
`!lockNamespace`, and `tests/mount-seam.test.tsx`'s `namespace-startup-` testid sweep is widened;
(3) not equivalent: the predecessor's slot outranked the configuration key, an overrides layer
below the environment cannot - an instance that sets `Fallen8__Namespaces__LoadOnStartup` by
environment is env-locked and the control says so (§4.3). The shipped compose does not set it.

## 4. The contract

### 4.1 The setting catalog is the one home

`Configuration/Fallen8SettingCatalog.cs`: one entry per configuration leaf key with `Key`, `Kind`
(`bool`/`int`/`double`/`string`/`enum`/`array`), `Bounds`, `AllowedValues`, `ApplyMode`, the excluding
`Rule` plus `Reason` for a never-writable key, and a non-null `ApplyNow` delegate for live entries.
`ApplyMode` is the single stored field and **`Tier` is derived from it**, so no entry can declare a
tier its apply semantics contradict; entries are built through one factory per tier, which is what
makes the invariants structural rather than tested.

| Rule | |
|---|---|
| 4.1.1 | **A governance test derives completeness**: reflect over the 16 `Fallen8*Options` classes in the apiApp assembly and fail unless every public leaf property is catalogued with a tier or listed `NotWritable` **with a reason**; assert `live + restart + notWritable == reflected leaf count`. Same shape as the MCP bridge-or-defer gate: a future option property forces a recorded decision. Hand-authored counts are never asserted anywhere. |
| 4.1.2 | **A `Live` entry with a null `ApplyNow` fails the suite.** "Declared live" is mechanically checkable, never aspirational. |
| 4.1.3 | **The catalog carries no human prose.** A key's meaning lives in its options class XML docs (already consumed by the OpenAPI pipeline); a second copy would be multi-home duplication. The UI links a key's owner doc. |
| 4.1.4 | **`AllowedValues` is load-bearing.** `Fallen8:Chat:Backend` is a free-form string that `ChatBackendFactory` switches on by exact ordinal match and throws otherwise, cached by a `Lazy<IChatBackend>` as a permanent 503. There is no `IValidateOptions` anywhere and binding a string never fails, so only the catalog's allowed-value set turns `"ollama"` into a `400`. |

### 4.2 Three tiers, four apply modes

**Tiers:** `Live` (writable, effective in this process), `Restart` (writable, effective next boot),
`NotWritable` (§4.7). The fourth apply mode is `never`, which is what a never-writable key stores and
what the tier derives from; the three modes below are the ones an operator can be promised.

| `applyMode` | Meaning |
|---|---|
| `live` | Takes effect for everything, immediately. |
| `liveForNewWork` | Takes effect for **new** work only. Required for honesty: `MaxSubscribers`/`SubscriberQueueSize` are read per subscribe, and `Plugins:MaxCount`/`StoredQueries:MaxCount` compare at registration and never evict, so lowering a cap changes nothing for existing holders. The copy names what is unaffected. |
| `restart` | Persisted now, applies at the next boot. |

### 4.3 The overrides layer: arbitration, not ordering

`Fallen8ConfigOverridesSource` persists `config.overrides.json` in `Fallen8:Metadata:Directory`
(beside `savegames.json` and `namespaces.json`) as a real `IConfigurationSource`, so restart-tier
writes genuinely apply at the next boot. The file holds no secret by construction (R1), which
keeps it off the secret-at-rest ledger.

| Rule | |
|---|---|
| 4.3.1 | **Outranks `appsettings.json`, `appsettings.{Environment}.json` and user secrets; never outranks environment variables or the command line.** It must beat appsettings because that file ships much of the writable set at its code defaults, so a layer underneath would be dead on most of the feature. It must not beat the environment because the shipped compose declares roughly two dozen `Fallen8__` keys and the docs instruct operators to set `Fallen8__` variables by hand. User secrets are a Development-only convenience and are deliberately outranked; the `source` field (4.3.4) makes that visible. |
| 4.3.2 | **Mechanism is per-key arbitration, not source ordering.** The source is appended last, but the provider emits a key only when no environment-variable or command-line provider **declares** it - probed by keeping provider references and `TryGet`ing the catalog key set, never inferred. A declared empty string is still a declaration, which handles compose's `${VAR:-}` idiom by construction. |
| 4.3.3 | **The provider no-ops unless its path resolves from configured metadata - NEVER `AppContext.BaseDirectory`.** That fallback would poison the shared test output directory used by 52 `WebApplicationFactory` files (outranking their `UseSetting` values for the whole run and every later run) and silently eat a dev operator's saves under `bin/`. Write-path tests use a per-test temp directory; the negative test is mandatory. |
| 4.3.4 | **Neither a silent no-op nor a silent override**, at three points: **write time** - `PATCH` answers `409` and writes nothing when any key in the batch is environment- or command-line-declared, naming each refused key, its authority and the exact `Fallen8__…` form (no force flag, no stored-but-shadowed value); **read time** - every descriptor carries `source` (`default`/`appsettings`/`userSecrets`/`environment`/`commandLine`/`override`), resolved by walking providers in reverse; **boot** - one log line per overridden key the environment outranks. |
| 4.3.5 | `reloadOnChange` is `false` on this source (the write path reloads explicitly; a watcher would race the pending-restart derivation), and **no writable key is an array** (providers merge arrays index-wise, so an override could never shrink an environment-provided list). |

### 4.4 `GET /config` grows the read surface

`ConfigREST` gains `settings[]` (per catalogued key: `key`, `kind`, `tier`, `applyMode`, `value`,
`source`, `restartPending`, `bounds`, `allowedValues`, `rule`, `reason`) and `pendingRestart[]`. A
never-writable key publishes `applyMode: never` and its `rule` beside the `reason`, so the UI can group
exclusions by rule instead of restating them per key.

**A `NotWritable` key publishes no value** (`valueWithheld: true`, plus key, tier, source, reason).
The route is anonymous on a keyless instance (it carries neither `[Authorize]` nor
`[AllowAnonymous]`, and the `FallbackPolicy` installs only when a key is configured), and today's
body is a deliberate allowlist - publishing everything would hand sidecar URLs and durability
paths to any unauthenticated caller. A test asserts no withheld value appears in the body.

Visibility ships before the write route and is independently valuable: it closes the
undocumented-keys gap and finally answers "what does `inherit` resolve to".

### 4.5 `PATCH /config` writes

A new **method on the existing `/config` path**, never a new path (`/config` is already in the
path-twinning gate's Fallen-8-level set; a new path would trip it), with `[Fallen8Level]` on the
action plus the sensitive-rate-limit and request-size attributes the `/plugins/*` actions use.

Order of operations: validate **every** key in the batch (catalogued, writable, in bounds, in the
allowed-value set) and refuse the whole batch before mutating anything; trial-bind against a
throwaway configuration root; write durably once; `Reload()`; run `ApplyNow` for `Live` entries;
return the **effective** value read back off the freshly bound options, so a coerced value is
visible. A `null` value clears the override and restores the layer below - that is the undo, and
no versioning exists. A `Restart`-tier write returns `200` and persists: never a `202`, never an
error, never a silent no-op. Unknown or never-writable keys are `400`, nothing written.

**Authorization - two independent operator acts.** A new
`Fallen8:Security:EnableConfigurationWrite` (default `false`) plus a capability policy, plus:

> **No API key configured means no configuration write, ever - even with the capability enabled.**

The new key is catalogued `NotWritable` under R1 like every other `Fallen8:Security` key, which is
what stops the write surface disabling or re-enabling itself. It is deliberately **not** exempted
from the catalog: an exemption would defeat the derived-completeness gate of 4.1.1, and it would buy
nothing, because R1 already refuses the write and 4.4 already withholds the value.

The existing capability policies add `RequireAuthenticatedUser` only when a key is configured; a
symmetric policy would make `PATCH /config` anonymously writable on the default deployment, and
unlike the per-request anonymous code execution, a configuration write persists a posture change
across restarts. The panel explains the requirement instead of showing a dead Save button.
Accepted consequence: the shipped `env:up` instance is keyless, so **the docs screenshot recipe
must set `F8_API_KEY`** or it photographs a read-only panel.

### 4.6 The pending-restart signal is derived, never stored

A `Fallen8ConfigOverrides` singleton captures a boot snapshot of every catalogued key's effective
value, taken immediately after the eager `Fallen8Namespaces` construction (the real latch moment).
A key is **pending** when its tier is `Restart` and its current effective value differs from that
snapshot. No marker file, no flag, no cleanup path: the pending set clears exactly when the
process restarts, is recomputed on every `GET`, and therefore survives page reloads and
reconnects by construction. Because a hand-edited `appsettings.json` also lights the banner, the
copy reads **"differs from the value this process started with"**, never "you changed this".

**No restart button and no restart endpoint**: a single-process self-hosted server has no
supervisor contract to restart into; the banner tells the operator what their own
`docker compose restart` will apply. *Revisit trigger:* a supervised deployment shape.

### 4.7 The never-writable set, generated by rules

Stated as rules so the list is derivable and a future key classifies itself:

| Rule | Excludes |
|---|---|
| R1 | **Nothing under `Fallen8:Security`.** Covers the lockout generators (`ApiKey` blank = permanent 401 with no REST recovery, `ApiKeyHeader`), the code-execution switch, the CORS perimeter, the rate-limit brakes, and the new capability gate itself. One rule beats per-knob carve-outs. |
| R2 | **No key that addresses on-disk state.** `Durability:StorageDirectory` (a **delete** path: `TryDrop` enumerates and `Directory.Delete`s under it), `WalPath` (orphans uncheckpointed commits), `CheckpointBaseName` (also the discovery glob), `Volatile` (selects the engine constructor), `Metadata:Directory` (locates the overrides file itself). |
| R3 | **No stored-data identity.** The embedding stamp (`ModelName`, `ModelVersion`, `Dimension`, `IntendedMetric` - a write silently corrupts stored vectors), everything that changes the embedding function under an unchanged stamp (`Backend`, `Ollama:Model`, `Onnx:MaxTokens`, `Onnx:Pooling`, `Onnx:Normalize`), and the index-identity keys (`Ingestion:EmbeddingName`, `VectorIndexId`, `FulltextIndexId`, `EntityIndexId` - changing one orphans a populated index and makes search silently empty). |
| R4 | **No URL the server dials** (SSRF): embedding, chat, Docling and NLP sidecar endpoints, `Observability:Otlp:Endpoint` (signal exfiltration), and `Integrations:Endpoint` (the sharpest: it is the base address of an authenticated pass-through proxy, so writable it becomes an arbitrary-URL proxy onto the operator's network). |
| R5 | **No capability flag.** `Embedding/Chat/Ingestion/Integrations:Enabled` are the operator's 403 opt-out; lifting one is privilege escalation. `Observability:Prometheus:Enabled` and `RequireApiKey` move only together (the latter defaults false; enabling the former alone opens an anonymous `/metrics`). |
| R6 | **No fleet-attribution key.** `Fallen8:Identity:*` bake into OpenTelemetry resource attributes at boot; a write only falsifies the identity of telemetry already sent under the real one. |
| R7 | **No dead knob.** `Security:AllowRemoteAccess` (documented "Reserved and currently NOT enforced") and `Nlp:MaxBatchSize` (bound, never read) advertise controls the app does not implement - the phantom-limit defect class. Implement or delete the property; never catalog it. |

### 4.8 Monitors go in per key, never per provider

**Hard rule, with a test.** Converting a consumer wholesale to `IOptionsMonitor.CurrentValue` makes
*every* key on that section live, including never-writable ones (production `appsettings.json`
reloads on change): `DurabilityLifecycleService` reads `Volatile`, `SaveOnShutdown` and
`CheckpointBaseName` together, so a wholesale conversion silently makes two R2 keys live. A
converted consumer reads **the specific live key** from `CurrentValue` and every other key from
the boot snapshot. The same rule forbids whole-section monitors for the chat and embedding
providers, whose `/status`-facing properties would then truthfully report a backend the process is
not using. The optional-`IOptions` plus `?? new T()` constructor pattern is preserved so
direct-construction tests keep working.

## 5. F8 Studio

The Configuration panel becomes a tiered editor and the codebase's first dirty-state form (no
`dirty`/`beforeunload` precedent exists in `src/`).

| | |
|---|---|
| 5.1 | A generic `SettingRow` rendered from the descriptor's `kind`, existing primitives only (`.input`, `.input w-auto`, `.label`, `.btn`) - no new CSS. |
| 5.2 | **`useConfig`'s 10 s `refetchInterval` is suspended while the form is dirty** (or the poll stomps inputs); `config-refresh` relabels to discard-and-reload while dirty. |
| 5.3 | Per-row `source` badge; an environment-declared row renders **disabled** with "set by `Fallen8__…` in the environment" (reusing the observability overlay's env-key caption vocabulary); a winning override gets a "set here" badge and a Clear action. |
| 5.4 | The pending-restart banner (`data-testid="config-pending-restart"`) is a state derived from the poll, not a toast; it discloses key, running value and pending value. |
| 5.5 | The editable region gates on **`!lockInstances`** (the panel is ungated today); the namespace-startup row additionally on `!lockNamespace`. |
| 5.6 | A failed `PATCH` renders inline at `config-settings-error`, never collapsing the panel into `config-unavailable`; new fields read defensively (`settings ?? []`). |
| 5.7 | **One home for the restart phrasing**: `src/lib/restartCopy.ts` exports the chip label and banner summary; `NamespacesPanel` imports it instead of borrowing by comment. |
| 5.8 | The subtitle drops "read-only"; the observability overlay's "this view is read-only" description is rewritten (it becomes the clearest mixed-tier demonstration). |
| 5.9 | Namespace fold-in: the `at startup` option renders `inherit (load)`/`inherit (skip)`; the `namespace-startup-hint` paragraph and testid are deleted; `NamespaceScope`'s prose names inherit-resolving-to-skip; the mount-seam testid sweep is widened. |

## 6. MCP (engine to REST to MCP)

`f8_admin` gains `get_settings` and `set_settings`, **doubly gated** on the server capability and
`F8_MCP_ENABLE_ADMIN`. The existing `GET /config` deferral entry is **deleted**, not narrowed (the
coverage gate's disjointness assertion fails when a bridged endpoint also matches a deferral), and
the OpenAPI snapshot is regenerated **before** adding bridge entries (the contract test requires
bridged routes to exist in the pinned snapshot). Accepted consequence: an agent can widen limits
that bound its own runs; the double gate is the mitigation, and §4.7 keeps authentication, storage
paths and stored-data identity out of reach. Separately, `f8_namespace` bridges the per-namespace
`loadOnStartup` field (§3.2).

## 7. Impact on existing features (mandatory sweep)

| Feature / layer | Impact | Action |
|---|---|---|
| Engine (`fallen-8-core`) | **No contract change.** `PluginRegistry.MaxCount` and `StoredQueryLibrary.MaxCount` are already settable, re-read at registration, documented as never evicting | No engine edit |
| [instance-config](../../done/instance-config/) | D6 retired (§3.1) | One status-header line; historical spec unchanged |
| [namespace-startup-default](../../done/namespace-startup-default/) | Superseded unimplemented (§3.2) | Already recorded there |
| [api-security-boundary](../../done/api-security-boundary/) | A key holder may now change the posture other callers see. Separately, R7 **deletes** the `Fallen8:Security:AllowRemoteAccess` flag that feature shipped, which no product code has ever read. Its startup warnings are untouched: they cover a missing API key and always-on code execution, and neither reads the flag | **Extend** the declared single home on the security docs page; rewrite its bind-address paragraph to say the flag is gone and why (its historical spec stays unrewritten) |
| [embedding-provider](../../done/embedding-provider/) | Its "503 until config changes" promise is restart-only in practice: the metric latch, dimension latch and cached `Lazy` creation exception have no reset path | State plainly on the page; claim no reload |
| [observability](../../done/observability/) / semantic pages | Several published read-only sentences become false | Amend in place |
| REST contract | New method on an existing path; field additions to `ConfigREST` and `NamespacesREST`; new `409` and `403` | `[Fallen8Level]`; existing problem+json homes |
| OpenAPI snapshot | Trips twice, once **silently**: the method trips the inventory, the field additions trip nothing | Regenerate in both phases, review diffs, always before MCP entries |
| MCP | Bridged (§6); deferral deleted | `McpBridgedEndpoints`, `f8_admin`, `f8_namespace` field |
| F8 Studio | §5; panel is ungated today | Including the `!lockInstances` gate |
| Docs site | New `configuration.md`: the ONE home for tiers, source resolution, pending-restart; README Key features line | **Keep** `running.mdx`'s table and per-feature key tables (operators configure by env before the UI exists); `configuration.md` lists no keys - the panel is the live inventory |
| Screenshots | `screen-connect.png` and `screen-connect-observability.png` bake read-only claims; new `screen-configuration.png` | Recapture both, add one; **set `F8_API_KEY`** (§4.5); the observability capture needs an OTLP-configured app |
| NL-assist dataset | **None** (no `Fallen8:*` NL-assist key exists) | No `RETRAIN-LOG.md` entry; fix the stale `ConfigurationPanel` docstring claiming the NL preference lives there |
| Architecture diagrams / browser-probe / provider-descriptor snapshot | **None** (no new channel or deployable; apiApp only; no descriptor change) | Verdicts recorded |
| Compose / env scripts | The declared `Fallen8__` keys are the arbitration input; no change needed | Syntax-check any touched script with `node --check`, never execute |
| Sibling deployables | Unreachable: `fallen-8-mcp` and `fallen-8-integrations` read their own prefixes | Docs page states the surface covers the apiApp only |
| Tests | The panel's read-only-ness and the security boundary are pinned by nothing today; a new write route gets zero automatic auth coverage | §4's pins plus hand-written security cases (plan phase 3) |

## 8. Non-goals (right-sized, with revisit triggers)

- **No restart endpoint or button** (§4.6).
- **No writable arrays** (4.3.5). *Revisit:* a writable key that is genuinely a list.
- **No `Analytics:MaxConcurrentRuns` live**: needs the `SemaphoreSlim` run gate rewritten and the
  429 text (which quotes the option) fixed together. Stays `Restart` so the message stays truthful.
  *Revisit:* an operator asking for live analytics parallelism. Same pattern:
  `Ingestion:MaxQueueLength` is quoted in the 503 while the real cap is an immutable bounded
  `Channel`, so it also stays `Restart`.
- **No `Observability:TracingSamplingRatio` live**: needs a custom sampler and would be live only
  where OTLP existed at boot - a worse contract than restart-required everywhere.
- **No second live tranche in v1**: nine ingestion ceilings, three NLP ceilings, the upload cap.
  Two change the chunk-boundary contract and leave mixed-pipeline chunks. *Revisit:* an operator
  tuning ingestion on a live corpus.
- **No history, audit log, diff view, export/import**: single-process self-hosted reality; the
  write is logged before-and-after. *Revisit:* a deployment with more than one operator.
- **No served-descriptor snapshot test.** Completeness is proven by 4.1.1, the route shape by the
  OpenAPI snapshot, and the catalog is reviewed compile-time C#; unlike the provider descriptors,
  no capture pipeline replays this JSON. *Revisit:* a docs capture that starts replaying it.
