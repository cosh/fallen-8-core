# Writable instance configuration - Implementation plan

Branch: `feature/writable-instance-config`. Spec: [spec.md](./spec.md).

Six slices; each ends with a green build and suite, and phases 1, 2, 3 and 5 are independently
shippable. Honest size: roughly 2,300 lines of product code plus ~1,000 of tests, two to three
weeks. Before any code: add the one D6-retirement line to instance-config's status header (docs
only, may commit straight to `main`).

## Phase 1 - the catalog, alone

`Configuration/Fallen8SettingCatalog.cs`: one entry per configuration leaf across the 16
`Fallen8*Options` classes - key, kind, bounds, allowed values, tier, apply mode, exclusion reason,
and the `ApplyNow` field. No human prose (spec 4.1.3). Includes the two `Fallen8:Namespaces`
startup keys as restart tier (spec §3.2) - the governance test forces them in here anyway.

**No entry is `Live` before phase 4.** A `Live` tier needs a working `ApplyNow`, which needs that
phase's monitor conversions, so every writable key starts `Restart` and phase 4 promotes the live
subset. That keeps 4.1.2 honest at every commit instead of parking aspirational live claims in the
catalog for three phases, and it is why phase 3 ships with restart tier as the whole UX.

Three tests are the point of the phase:

1. The governance sweep (spec 4.1.1): reflect over the apiApp's options classes; every public leaf
   is catalogued or `NotWritable` with a reason; `live + restart + notWritable == reflected count`.
2. `EveryLiveEntry_HasAnApplyDelegate` (spec 4.1.2).
3. Every §4.7 key is absent from the writable set.

Forces the two R7 dead-knob decisions (`Security:AllowRemoteAccess`, `Nlp:MaxBatchSize`):
implement or delete the property before anything can render it. Both were **deleted** (verified read
by nothing repo-wide), following the precedent of the removed `MaxSensitiveRequestBodyBytes` knob, so
the deletions also touch `appsettings.json`, the security docs page and the api-security-boundary
feature README, and each absence is pinned by a test.

Gates: build, `dotnet test`, plus the docs-site build because the R7 deletion edits a published page.

## Phase 2 - the overrides source and the READ surface

1. `Fallen8ConfigOverridesSource`/`Provider` - the repo's first custom `IConfigurationSource`:
   per-key arbitration (spec 4.3.2), `reloadOnChange: false`, and **no `AppContext.BaseDirectory`
   fallback** (4.3.3 is the suite-poisoning landmine - write its negative test in this phase).
2. `Fallen8ConfigOverrides` singleton: path resolution, load, per-key source resolution, and the
   boot snapshot taken immediately after the eager `Fallen8Namespaces` construction (4.6).
3. `GET /config` grows `settings[]` and `pendingRestart[]`, values withheld for never-writable
   keys (4.4), with the no-withheld-value-in-body test.
4. `NamespacesREST` gains `loadOnStartupDefault` and `startupLoadMode`, **uncomposed** (spec §3.2).

No write route yet; independently valuable (every key visible with tier, value, source).

Gates: build, `dotnet test`, `scripts/update-openapi-snapshot.ps1` (field additions trip no test
and would stale the snapshot silently), `JsonSourceGenParityTest` for the new DTOs.

## Phase 3 - the WRITE route, the capability, and MCP

1. `PATCH /config` on the existing path: `[Fallen8Level]`, sensitive-rate-limit and request-size
   attributes, one merged `<remarks>` (two `<remarks>` fail the suite).
2. Validate-everything-before-mutating, trial-bind, one durable write, `Reload()`,
   effective-value read-back, `409` arbitration, `400` on out-of-domain values (the
   `Chat:Backend` case trial-binding cannot catch).
3. `Fallen8:Security:EnableConfigurationWrite` (default false), the capability policy, the
   no-key-means-403 rule (4.5). **Catalogued `NotWritable` under R1**, not exempted: an exemption
   would break phase 1's derived-completeness gate and buy nothing, since R1 already refuses it.
4. MCP in the same phase or the coverage gate goes red the moment the route exists: `f8_admin`
   gains `get_settings`/`set_settings`, `McpBridgedEndpoints` gains both, the `GET /config`
   deferral is **deleted**, and `f8_namespace` bridges per-namespace `loadOnStartup`. Regenerate
   the OpenAPI snapshot **before** adding the bridge entries.

After this phase every writable key is restart tier and the pending banner is the whole UX - a
complete, honest product.

Gates: build, `dotnet test`, OpenAPI snapshot (method), MCP coverage + contract tests, the
anonymous-operations assertion (the new route joins no anonymous set), plus five hand-written
security cases: anonymous with a key configured is 401; valid key with capability off is 403;
capability on with no key configured is 403; an environment-declared key is 409 with nothing
written; a non-catalogued or never-writable key is 400 with nothing written.

## Phase 4 - the live tier

Promote the live subset from `Restart` to `Live` with an `ApplyNow` each, one key at a time, via
per-key monitor conversions across roughly 9 files honouring spec 4.8 (**per key, never per
provider**; other keys on a converted section keep reading the boot snapshot; the
optional-`IOptions` constructor pattern is preserved). Two fan-outs need no signature change:
assign onto the shared mutable change-feed options object the dispatcher re-reads per subscribe,
and walk loaded engines assigning the plugin and stored-query ceilings (also updating the
boot-latched fields so a later-activated namespace gets the current value). `liveForNewWork`
applies to exactly the never-evicting caps (4.2).

**Every live key gets a test asserting observed behaviour changed** - a subscribe is refused, a
create 422s - never merely that the option value changed.

Gates: build, `dotnet test`.

## Phase 5 - Studio, plus the honesty sweep that rides with it

Per spec §5: the generic `SettingRow`, the first dirty-state form with the poll suspended while
dirty, source badges and env-locked captions, the pending-restart banner, the `!lockInstances`
gate, inline errors that never collapse the panel, `src/lib/restartCopy.ts`, and the namespace
fold-in rows (5.9: `inherit (load)`/`inherit (skip)`, hint deleted, testid sweep widened).

Same commit: retire the published read-only claims, recapture `screen-connect.png` and
`screen-connect-observability.png`, add `screen-configuration.png`. **Set `F8_API_KEY`** in the
capture recipe or the editor does not render (4.5); the observability capture needs an
OTLP-configured app; never pipe the background app launch through `Select-Object -First N`.

Gates: `npx tsc --noEmit` and vitest with exit codes confirmed via
`cmd /v:on /c "... & echo EXIT=!ERRORLEVEL!"`; the endpoint-contract sweep (the new client export
exercised or excluded with a reason); screenshots.

## Phase 6 - docs and close-out

`docs/src/content/docs/configuration.md` as the one home for the tier model, source resolution and
the pending-restart signal, registered in the sidebar; root README Key features line. **Keep**
`running.mdx`'s table and the per-feature key tables; `configuration.md` lists no keys. Amend the
observability, studio, semantic and security pages in place; extend (never restate) the security
posture paragraph at its single home. Move this directory to `features/done/`, status updated,
deviations recorded below.

Gates: `npm --prefix docs ci && npm --prefix docs run build` (link-checked).

## Follow-ups, deliberately not in this branch

- **Live tranche 2**: the ingestion/NLP ceilings and the upload cap (spec §8).
- **Extract the durable read-modify-write discipline**: the overrides writer is the third caller
  of the pattern (save-game registry, namespace catalog). Landing that refactor inside this PR
  makes it un-bisectable; write the third copy with a pointer here, extract in a PR containing
  nothing else.

## Before declaring the wire contract done

A mocked client test cannot catch a wrong body shape: `curl` `PATCH /config` and `GET /config`
against a live apiApp, including the `409` path with a `Fallen8__` variable actually set. Run a
second adversarial pass over the **fixes**, not just the code. Confirm on Linux non-root (CI is
Linux; the durable write and directory creation are filesystem-sensitive).

## Left open

**Phase 1 (landed).** Deviations from the spec as written, each folded back into the spec text in the
same commit rather than left as a difference:

1. **`Tier` is derived, not stored.** The entry stores one `ApplyMode` and derives `Tier`, so a
   contradictory pair cannot exist. `ApplyMode` therefore has a fourth value, `never`, and the spec's
   §4.1, §4.2 and §4.4 were amended to match. Entries are built through one factory per tier, which
   moved the invariants from tests into the type.
2. **Two fields the spec did not name**: `Rule` (the excluding rule, so the UI and docs can group
   exclusions instead of restating them) and an `array` kind (§4.3.5 needs a way to say so).
3. **`Fallen8:Security:EnableConfigurationWrite` is catalogued** rather than exempt from the catalog,
   because an exemption would defeat the derived-completeness gate and buy nothing under R1. Spec §4.5
   amended.
4. **No entry is `Live` yet**, by design (see phase 1 above); phase 4 promotes the live subset.
5. **The leaf definition is the binder's, not the obvious one.** A get-only nested block or collection
   IS bound by `Microsoft.Extensions.Configuration` (measured, and now pinned by a test), so a
   setter-only sweep would have left a hole exactly where the gate promises there is none. The sweep
   also reports any property shape it cannot classify, so a future exotic option fails loudly.
6. **The docs-site build joined phase 1's gates**: R7's deletion pulled the security page's
   bind-address paragraph forward from phase 6.
7. **Counts are measured now, not estimated**: 94 leaves, 44 never-writable, 50 restart, 0 live.

**Phase 2 (landed).** Deviations and decisions, all recorded rather than inferred:

1. **A seventh `source` value, `host`.** An in-process host setting (a test host's `UseSetting`, an
   embedding host) is reported as `host`, not `commandLine`. Arbitration only stands down for a real
   environment variable or command line, so calling a host setting `commandLine` would tell an
   operator a row was locked when a write to it would in fact succeed. Exactly two sources now mean
   locked, and a test pins that the read-only rule and the write outcome agree. Spec 4.3.4 amended.
2. **The path rule is stricter than "read the metadata directory".** The layer resolves a path ONLY
   from an explicitly configured `Fallen8:Metadata:Directory` and never through
   `Fallen8MetadataOptions.ResolveDirectory`, whose documented default is a folder under
   `AppContext.BaseDirectory`. Measured consequence of getting this wrong: 38 of the 43
   `WebApplicationFactory` test files declare no metadata directory, and the shared test output
   directory already holds a 66 KB `savegames.json`, so a file there would have outranked every test
   host's settings for that run and every later run. The compose deployment sets the directory, and an
   instance that has not also has no API key, so it accepts no write to persist.
3. **A corrupt overrides file is reported and ignored, never fatal.** The save-game registry and the
   namespace catalog both throw on a corrupt document, and that is right for them: each is the sole
   authority for what exists. This file carries preferences, and a provider that threw would do so
   during configuration build, before the logging pipeline exists, leaving the instance unbootable
   with no REST recovery. Boot logs the failure instead.
4. **The layer is bounded by the catalog's writable set.** A hand-edited never-writable or unknown key
   in the file is refused and logged, so the file cannot become a way around section 4.7.
5. **No DTO for the overrides file.** The provider only reads, so it parses with `JsonDocument`: no
   serializer-context registration, no parity-test entry, and a hand-written scalar of any JSON type
   is accepted verbatim.
6. **`Fallen8Namespaces` exposes the two startup values as properties** rather than storing them
   twice, and `GET /ns` publishes the BOOT-latched values: they describe what this boot actually did,
   while the pending write shows up on `GET /config` with its restart-pending flag. `startupLoadMode`
   is a camelCase string because this app installs no string-enum converter and a bare enum would
   publish an integer.
7. **`withheld value` is asserted over `settings[]`, not the whole body.** `GET /config` has published
   `observability.otlpEndpoint` and the embedding identity stamp since feature instance-config, and
   both are never-writable keys, so a whole-body search would fail on behaviour this feature neither
   introduced nor changes. Worth a decision later: on a keyless instance that route is anonymous, so
   those pre-existing fields reach an unauthenticated caller.
8. **Deferred to phase 5, deliberately**: the web-ui `types.ts` mirror of the two new `ConfigREST`
   fields. They are optional on the wire and no web-ui gate reads them yet, and phase 5 is the Studio
   phase that will consume them.

**Phase 3 (landed).** Decisions, three of them the owner's:

1. **The no-key rule lives in the ACTION, not the policy.** A policy that denies an unauthenticated
   caller challenges rather than forbids, so a keyless instance answered `401` and invited the caller
   to authenticate with a key that does not exist. The capability stays in the policy; the action
   answers `403` and names both settings. All five authorization cases are hand-written tests, because
   the repository's security-boundary test is spot checks rather than a route sweep and a new write
   route gets no automatic coverage.
2. **`value` became the EFFECTIVE value**, which corrects phase 2. Binding reports null for a section
   absent from every configuration file, and roughly a quarter of the catalogue is in that position, so
   the panel would have shown an empty field for a setting fully in force. The owning options class is
   bound (filling in its property defaults) and the value read off the property; the boot snapshot uses
   the same values, which also makes writing a key to the value it already had correctly NOT pending.
   Verified live: `runningValue` for `Plugins:MaxCount` reads 64, which exists in no file.
3. **Owner decision: the pre-existing anonymous `/config` exposure stays**, documented rather than
   changed. `observability.otlpEndpoint` and the embedding identity stamp are never-writable values
   published since feature instance-config, and a keyless instance already grants the same caller
   anonymous in-process code execution, so withholding a value they could read by running code buys no
   real protection. The withholding rule in `settings[]` is defence in depth against casual exposure,
   not a boundary. Phase 6 puts that reasoning on the docs page.
4. **Owner decision: the container image now sets `Fallen8__Metadata__Directory`.** It closes a real
   `docker run` gap and, incidentally, a pre-existing one: without it the save-game registry landed in
   the container's own filesystem and disappeared on recreation, because `Metadata:Directory` does not
   follow `StorageDirectory`. A host with no directory configured gets `409` naming the setting.
5. **Owner decision: the write answers per-key results plus the pending set**, so a coerced value is
   visible next to what was asked for and the restart banner needs no second round trip.
6. **`Fallen8OptionsSections` is written out, not reflected over.** Reflecting on each class's
   `SectionName` cannot be annotated for trimming and would need a suppression; the section names still
   come from the constants, and `SettingCatalogTest` fails if a new options class misses the map, which
   is what keeps trial-binding honest.
7. **A shadowed key stays in the file** when the batch is rewritten. Dropping it would delete an
   operator's stored intent the moment they set an environment variable that outranks it.
8. **Verified against a live server**, not only in-process: the 200 with its read-back value, the
   `409` naming the real `Fallen8__StoredQueries__MaxCount` variable, that the valid key in that same
   refused batch was NOT written, the `400` naming rule R2, and that a never-writable key's `value`
   property is genuinely absent from the wire body.

**Phase 4 (landed).** Six keys promoted, all `liveForNewWork`, plus the mechanism itself:

1. **No monitor conversions at all**, which is a real departure from this plan's own wording. Two
   findings made the monitor route wrong rather than merely unnecessary. First, `IOptions<T>` is a
   process singleton and most consumers hold that instance, so a monitor handing out a NEW instance on
   reload would leave every consumer that captured `.Value` at construction reading the old one, which
   is the opposite of live. Second, measured: an apply delegate that read
   `IOptionsMonitor.CurrentValue` got the value from BEFORE the reload, because the monitor invalidates
   its cache from the same reload token the delegates run on and callback order is registration order.
   So a delegate binds a FRESH options instance from configuration and assigns the one property it owns
   on the object the consumer already reads. That is per key by construction, with no ordering
   relationship to anything, and it needs no consumer edits at all.
2. **The apply runs on every configuration reload, not only on a write** (`Fallen8LiveSettings`).
   `appsettings.json` reloads on change in production, so a hand-edited file moves what `GET /config`
   publishes; if the apply ran only from the write path, a live key's published value would then differ
   from the value in force, and the pending signal deliberately says nothing about live keys, so
   nothing would flag it. A test pins that a reload this process did not initiate still applies.
3. **A failing delegate never fails the write.** By then the value is persisted and reloaded, so the
   honest outcome is a live key that did not take effect: the failure is recorded, logged, reported as
   `applyFailure`, and the key's promise is downgraded from live to restart in the same response.
4. **The tranche is six keys**: the change feed's `MaxSubscribers`, `SubscriberQueueSize` and
   `KeepAliveSeconds`, the `Plugins` and `StoredQueries` registration ceilings, and
   `Namespaces:MaxNamespaces`. Every one is `liveForNewWork`, never plain `live`, because each is a cap
   consulted when work starts: none evicts a subscriber, a registration or a namespace. `BufferSize`
   stays restart-tier because its ring is allocated at engine construction, and `ChangeFeed:Enabled`
   because it decides the feed exists.
5. **One shared object, not a walk over engines.** `ChangeFeedOptions` is projected once and handed to
   every engine, so assigning one property reaches every namespace including one activated later. The
   registry ceilings genuinely need a fan-out, and it also moves the boot-latched values a NEW engine is
   built with, or a namespace activated after the write would come up with the old ceiling.
6. **Deferred with a reason, not by omission**: the request-bound tranche (the statistics knobs, the
   two analytics time budgets, the BulkIO bounds). They are read per request from the options singleton,
   so each is a one-line delegate, but the spec warns that a key quoted in a user-facing message must
   stay restart-tier or the message lies about the cap in force, and that check is exactly what the
   analysis for that group had not finished when this phase closed. Do it before promoting them.
7. Every promoted key's test asserts OBSERVED behaviour: a subscribe refused then allowed, a
   registration refused then allowed, a namespace creation refused then allowed, and a new
   subscription's queue holding more events than one created before the write. None of them can pass by
   reading an option value back, and all of them failed while the apply mechanism was broken.

**Phase 5 (code landed; the screenshot recapture is still owed).** What shipped and what did not:

1. **The editor**, as spec 5.1 to 5.8: a generic `SettingRow` rendered from the descriptor's `kind`
   using only existing primitives, per-row source badges, an environment-locked row rendered disabled
   with the exact `Fallen8__…` spelling to remove, a Clear for a row whose stored value is the one in
   force, the derived pending-restart banner disclosing running and pending values, inline write errors
   at `config-settings-error` that leave the read surface standing, and the `!lockInstances` gate.
2. **The poll is suspended while dirty**, done by widening `useConfig` with a `poll` flag rather than
   editing the panel: the hook is shared and had exactly one consumer, and `refetchInterval: false` is
   reactive in the query library this repo pins.
3. **`src/lib/restartCopy.ts` is the one home** for restart phrasing, which previously existed in four
   places, one of them a comment saying it was borrowing another view's register.
4. **The namespace fold-in**: `inherit` now resolves in the label. It reads `inherit (load)` or
   `inherit (skip)` from the instance default, and under startup mode `all` or `defaultOnly` it says
   the MODE decides instead, because those short-circuit every per-namespace preference and a label
   composed from the default alone would be a confident lie. That is what the two uncomposed `/ns`
   fields are for, and they are now mirrored in `types.ts`.
5. **The deleted hint's fact was moved, not dropped.** `namespace-startup-hint` is gone; what only it
   could say (how a not-loaded namespace behaves) survives as `namespace-startup-note`, and the test
   that pinned the old paragraph now pins the new home plus the hint's absence.
6. **A vacuous assertion was deliberately NOT added** to the mount-seam sweep. That suite stubs fetch
   to throw, so `GET /config` always fails there and the panel renders zero setting rows whether or not
   its gate exists: an assertion would pass for the wrong reason. The comment in that file now says so,
   and the gates are asserted against a panel with real data instead.
7. **Two keys take the namespace lock as well as the instance lock**
   (`Namespaces:LoadOnStartup`, `Namespaces:StartupLoadMode`): the Configuration panel is not behind
   `lockNamespace`, so an embed that locked namespace management could otherwise re-plan the host's
   next boot through them.
8. **A naming collision found and avoided**: `SettingKind` was already taken by the integrations
   feature, so the four new unions carry a `ConfigSetting` prefix.
9. **STILL OWED**: recapture `screen-connect.png` and `screen-connect-observability.png` (both bake a
   read-only claim that is now false) and add `screen-configuration.png`. The recipe needs an app with
   an API key AND `Fallen8:Security:EnableConfigurationWrite=true` AND a metadata directory, or the
   editor renders read-only and the screenshot photographs the wrong thing; the observability capture
   additionally needs an OTLP-configured app or its Push section renders "off".

## Follow-up this phase uncovered (not fixed here)

**NLP enrichment silently stops above 512 chunks.** R7 deleted `Fallen8:Nlp:MaxBatchSize` because no
code read it, and doing so surfaced what it was meant to bound: `NlpClient` posts every chunk of a
document as ONE enrich request, the sidecar refuses more than `F8_NLP_MAX_ITEMS` (512) items with a
413, `Fallen8:Ingestion:MaxChunksPerDocument` allows 2000, and enrichment failures are additive-only.
So a document over 512 chunks gets no enrichment and nothing says so. The fix is to batch the call in
`NlpClient`, which is a behaviour change with its own tests and its own before-and-after measurement;
it does not belong in a catalog phase. Until then the limitation is recorded in the options class and
in `SettingCatalogTest`, and the ingestion docs page should state it.
