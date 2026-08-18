# Writable instance configuration - Implementation plan

Branch: `feature/writable-instance-config`. Spec: [spec.md](./spec.md).

Eight slices. Each ends with a green build and suite, and phases 1, 2, 3 and 5 are each
independently shippable products. Honest size: roughly 2,300 lines of product code across ~25 files
plus ~1,000 lines of tests, and the documentation and copy tail is comparable in effort to phase 4's
plumbing. Two to three weeks of focused work.

## Phase 0 - the record (docs only)

This file and [spec.md](./spec.md). Retire instance-config D6 in writing by invoking its revisit
clause and naming the operator need; record the supersession of `namespace-startup-default`; fix the
vocabulary boundary now, before any code borrows the wrong word: this feature says **"live"** and
**"restart-required"**, and must not reuse "reconfigurable", which `fleet-observability` already spent
on environment-plus-`env:up` while explicitly denying hot reload - a statement that stays true here.

No code. May commit straight to `main` per repo convention.

## Phase 1 - the taxonomy, alone

`Configuration/Fallen8SettingCatalog.cs`: one entry per configuration leaf across every options
class, carrying key, kind, bounds, allowed values, tier, apply mode, exclusion reason, and an
`ApplyNow` delegate for live entries. No human prose (spec 5.1.3).

Three tests, and they are the point of the phase:

1. The governance sweep: reflect over every options class in the **test** assembly and fail unless
   every public leaf is catalogued or listed `NotWritable` with a reason. Assert
   `live + restart + notWritable == the reflected leaf count`, so the totals are derived.
2. `EveryLiveEntry_HasAnApplyDelegate`.
3. Every never-writable key from spec §5.7 is absent from the writable catalog.

Reviewable as pure data plus tests, no behaviour change. It also forces the two dead-knob decisions
(`Security:AllowRemoteAccess`, `Nlp:MaxBatchSize`) before anything can render them: implement or
delete the property, per R7.

Gates: build, `dotnet test`.

## Phase 2 - the overrides source and the READ surface

1. `Fallen8ConfigOverridesSource` / `Provider` - the repo's first custom `IConfigurationSource`.
   Per-key arbitration against the environment-variable and command-line providers (spec 5.3.2),
   `reloadOnChange: false`, and **no `AppContext.BaseDirectory` fallback** (5.3.3 - this is the
   suite-poisoning landmine, so write the negative test in this phase).
2. `Fallen8ConfigOverrides` singleton: path resolution, load, per-key source resolution by walking
   providers in reverse, and the boot snapshot taken immediately after the eager `Fallen8Namespaces`
   construction (5.6).
3. `GET /config` grows `settings[]` and `pendingRestart[]`, with values withheld for never-writable
   keys (5.4.1) and a test asserting no withheld value appears in the body.
4. The descriptor snapshot plus its regenerate script (5.1.5).

**No write route yet, and this phase is independently valuable**: it makes every key visible with
tier, effective value and source, which closes the undocumented-keys gap and answers the original
"what does inherit resolve to".

Gates: build, `dotnet test`, `scripts/update-openapi-snapshot.ps1` (the **field** additions trip no
test and would otherwise stale the pinned snapshot silently), `JsonSourceGenParityTest` for the new
DTOs.

## Phase 3 - the WRITE route, the capability, and MCP

1. `PATCH /config` on the existing path with `[Fallen8Level]`, the sensitive-rate-limit and
   request-size attributes, one merged `<remarks>` (a doc block with two `<remarks>` fails the
   suite, and .NET 10's XML reader publishes only the first anyway).
2. Validate-everything-before-mutating, trial-bind, one durable write, `Reload()`, effective-value
   read-back, `409` arbitration, `400` on out-of-domain values (5.1.4 - the `Chat:Backend` case that
   trial-binding cannot catch).
3. `Fallen8:Security:EnableConfigurationWrite` (default false, absent from the catalog) plus the
   capability policy and the **no-key-means-403** rule (5.5.2).
4. MCP in the same phase, or the coverage gate turns the suite red the moment the route exists:
   `f8_admin` gains `get_settings` and `set_settings`, `McpBridgedEndpoints` gains both, and the
   `GET /config` deferral is **deleted**. Regenerate the OpenAPI snapshot **before** adding the
   bridge entries.

At the end of this phase every writable key is restart-tier and the pending banner is the entire UX -
a complete, honest, reviewable product.

Gates: build, `dotnet test`, the OpenAPI snapshot (method), the MCP coverage and contract tests, the
anonymous-operations security assertion (the new route must carry no operation-level security
override and must not join the pinned anonymous set), plus five hand-written security cases: anonymous
is 401 with a key configured; valid key with the capability off is 403; capability on but no key
configured is 403; an environment-declared key is 409 with nothing written; a non-catalogued or
never-writable key is 400 with nothing written.

## Phase 4 - the live tier

Per-key monitor conversions across roughly 9 files, honouring spec §5.8 (**per key, never per
provider**; every other key on a converted section still reads the boot snapshot; the optional
`IOptions` plus `?? new T()` constructor pattern is preserved so direct-construction tests keep
working).

Two fan-outs need no signature change: assign onto the shared mutable engine-side change-feed options
object, which the dispatcher already re-reads per subscribe; and walk loaded engines assigning the
plugin and stored-query ceilings while also updating the boot-latched fields, so a namespace
activated later gets the current value.

`liveForNewWork` is applied to exactly the never-evicting caps (5.2).

**Every live key gets a test asserting observed behaviour changed** - a subscribe is refused, a create
422s, the response reports a coerced value - never a test that merely asserts the option changed.

Gates: build, `dotnet test`.

## Phase 5 - Studio, plus the honesty sweep that must ride with it

Per spec §6: the generic `SettingRow`, the first dirty-state form with the poll suspended while
dirty, per-row source badges and env-locked captions, the pending-restart banner, the
`!lockInstances` gate, inline error handling that does not collapse the panel, and
`src/lib/restartCopy.ts` as the single author of the restart phrasing.

Same commit: retire the published read-only claims across all layers, and recapture
`screen-connect.png` plus `screen-connect-observability.png`, adding `screen-configuration.png`.
**Set `F8_API_KEY`** in the capture recipe or the editor does not render (5.5.2). The observability
capture must run against an OTLP-configured app or its Push section renders "off" and destroys the
good image. Never pipe the background app launch through `Select-Object -First N`.

Gates: `npx tsc --noEmit` and vitest with exit codes confirmed via
`cmd /v:on /c "... & echo EXIT=!ERRORLEVEL!"`; the endpoint-contract sweep (the new client export
must be exercised or excluded with a reason); screenshots.

## Phase 6 - fold in namespace-startup-default

`Fallen8:Namespaces:LoadOnStartup` and `StartupLoadMode` become restart-tier catalog entries.
`NamespacesREST` gains `loadOnStartupDefault` and `startupLoadMode`, **uncomposed** (predecessor 4.5).
The `at startup` option renders `inherit (load)` / `inherit (skip)`; the `namespace-startup-hint`
paragraph and testid are deleted; `NamespaceScope`'s prose is reworded; `mount-seam.test.tsx`'s
testid sweep is widened; the namespace-startup row gates additionally on `!lockNamespace`.

Gates: `dotnet test`, OpenAPI snapshot (fields), web-ui tests.

## Phase 7 - docs and close-out

`docs/src/content/docs/configuration.md` as the one home for the tier model, source resolution and
the pending-restart signal, registered in the sidebar, plus the root README Key features line.
**Keep** `running.mdx`'s table and the per-feature key tables; `configuration.md` deliberately lists
no keys. Amend the observability, studio, semantic and security pages in place, and extend (never
restate) the security posture paragraph at its declared single home.

Move `features/open/writable-instance-config/` to `features/done/`, status line updated, deviations
recorded per phase below.

Gates: `npm --prefix docs ci && npm --prefix docs run build` (link-checked).

## Follow-ups, deliberately not in this branch

- **Live tranche 2**: nine ingestion ceilings, three NLP ceilings, the upload cap. Two of them change
  the chunk-boundary contract for new documents and leave mixed-pipeline chunks recorded in a
  namespace's signature.
- **Extract the durable read-modify-write discipline.** The overrides writer is the **third** caller
  of the same pattern (the save-game registry and the namespace catalog are the first two), so the
  no-duplication rule genuinely fires here. But those two persist the sole authority for what loads
  at startup and the only namespace inventory, and a subtle change to either is a data-visibility bug
  that surfaces one restart later. Landing that refactor inside a PR that also adds a REST write
  surface, a configuration provider, nine monitor conversions and a rewritten Studio panel makes it
  un-bisectable. **Write the third copy now with a pointer to this follow-up, and extract it in a PR
  containing nothing else.**

## Verdicts recorded rather than run

`tools/browser-probe` not applicable (apiApp only; nothing gated on
`HostCapabilities.SupportsBackgroundWork`); provider-descriptor snapshot untouched; architecture
diagrams unchanged; no NL-assist dataset change and no `RETRAIN-LOG.md` entry owed.

## Before declaring the wire contract done

A mocked client test cannot catch a wrong body shape: `curl` `PATCH /config` and `GET /config`
against a live apiApp, including the `409` arbitration path with a `Fallen8__` variable actually set.
Run a second adversarial pass over the **fixes**, not just the code. Confirm on Linux non-root, since
CI is Linux and both the durable write and the directory creation are filesystem-sensitive.

## Left open

Nothing yet - this section records deviations and unfinished rows as the phases land.
