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
implement or delete the property before anything can render it.

Gates: build, `dotnet test`.

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
3. `Fallen8:Security:EnableConfigurationWrite` (default false, absent from the catalog), the
   capability policy, the no-key-means-403 rule (4.5).
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

Nothing yet - this section records deviations as the phases land.
