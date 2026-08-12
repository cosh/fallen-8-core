# F8 Studio - embeddable in a host SaaS portal

Status: done (2026-08-12). All six phases implemented: the seams (phases 1-5) and the library
packaging (phase 6) - the artifact ships from `npm run build:lib`, a bare host application
consumes it in CI on every push (exports map, peer resolution, scoped styles, editor, canvas,
unmount), and the user-facing page is
<https://cosh.github.io/fallen-8-core/embed-studio/>. Related: [web-ui](../../done/web-ui/),
[standalone-ui](../../done/standalone-ui/), [graph-namespaces](../../done/graph-namespaces/),
[nl-assist-ux](../../done/nl-assist-ux/), [api-security-boundary](../../done/api-security-boundary/).

## Motivation

F8 Studio (`fallen-8-web-ui`) is a standalone SPA, served either from the database's own
`apiApp/wwwroot` (`npm run build:apiapp`) or as its own container pointed at any REST origin
(feature standalone-ui). A host SaaS portal that offers hosted Fallen-8 instances will want to
hand each user a Studio for their instance. This feature makes sure both ways of doing that are
covered, and adds the seams for the deeper one.

It is deliberately *additive*: the standalone app must keep behaving exactly as it does today,
every seam defaults to the current behavior, and the clone-from-GitHub experience (build, run,
open the same-origin Studio, no auth) stays untouched. There is **no rewrite** and **no
micro-frontend framework** here, just the minimum set of injection points a host needs.

## Two embed shapes

A host portal has two ways to surface Studio, and they need very different amounts of work:

1. **Studio at its own origin (link-out).** The portal links to a Studio deployment (for
   example on a subdomain) whose runtime `config.js` points it at the tenant's instance. This
   shape **already works today with zero new code**: feature standalone-ui ships the config
   seam (`window.__F8_CONFIG__`), and the portal provisions per-tenant config. The seams below
   still improve this shape (bearer auth, pinned namespace, locked instance list), but nothing
   blocks it.
2. **Studio inside the portal's shell (in-shell module).** The portal renders Studio as a
   module inside its own routing, chrome, auth, and theme. This is what the rest of this spec
   enables via a mount API and a `StudioConfig` contract.

## Starting position (already good - do not regress)

- **One transport choke-point.** All server access goes through `apiRequest` in
  [`src/api/client.ts`](../../../fallen-8-web-ui/src/api/client.ts); base URL and auth headers are
  read per-instance (`instance.baseUrl`, `authHeaders(instance)`), never from a global constant.
- **Auth is an extensible union.** `InstanceConfig.auth` (`src/instances/types.ts`) is a discriminated
  union (`none | apiKey`) whose documented seam is exactly where the bearer arm below plugs in.
- **A config-injection seam exists** (feature standalone-ui): the managed default instance is
  synthesized from `config.js` on every load and never persisted; only personal instances reach
  `localStorage`.
- **Namespace binding exists** (feature graph-namespaces): the registry tracks a per-instance
  active namespace and namespace support, `InstanceConfig.namespace` addresses `/ns/{ns}/…`,
  and `useInstanceStore()` returns namespace-bound instance views with per-namespace workspace
  stores.
- **The graph canvas is already presentational.** `src/canvas/GraphCanvas.tsx` takes nodes,
  edges, a `StyleConfig`, optional overlays, and an `onSelect` callback; it imports only types
  from the workspace store. All instance/query wiring lives in `CanvasScreen`.
- **Server state is isolated** behind TanStack Query; client state behind Zustand.
- **`window`/`document` use is minimal** (canvas/anchor/setTimeout only) - not a coupling blocker.

## The seven couplings this feature removes

Each is a real, verified coupling. The **contract** column is the observable behavior that must
stay identical for the standalone app.

| # | Coupling | Where | Seam to add | Standalone contract |
|---|----------|-------|-------------|---------------------|
| 1 | App owns bootstrap (QueryClient, RouterProvider, StrictMode, `#root` mount) | `src/main.tsx` | Export `mountStudio(el, config)` / `<F8Studio config>`; `main.tsx` becomes a thin caller with default config | Same DOM, same providers, same defaults |
| 2 | App owns the URL at root paths | `src/app/routes.tsx` (`router`, no basepath) | Configurable router `basepath` (default `""`); optional memory-history mode | Root-path routes unchanged |
| 3 | Global, unscoped CSS (Tailwind preflight + `body` + generic `.btn/.panel/.input`) | `src/index.css` | Scope the generic-named primitives under a `.f8-studio` root container (`:where()`, zero specificity change); standalone wraps its root in the same scope. Preflight stays global in the standalone build - only the packaged library artifact ever meets a host DOM, so a scoped preflight ships with the packaging phase | Pixel-identical standalone |
| 4 | Hard-coded dark theme (fixed hex `@theme` tokens, fixed type stack, forced `html.dark`) | `src/index.css`, `index.html` | Theme tokens (surfaces, semantic accents, **and type**) become CSS custom properties defaulting to today's values; host may override; enables a future light theme | Same dark palette and type by default |
| 5 | Module singletons + fixed `localStorage` keys (`f8.instances`, `f8.workspace.<id>`, `f8.nl-assist`, `f8.first-run`) with no host injection point | `src/instances/registry.ts`, `src/state/instanceStore.ts`, `src/delegate/nl/config.ts`, `src/firstrun/firstRunStore.ts` | `StudioConfig` context supplying instance(s)/credentials and a storage-key namespace prefix; a host-supplied instance can seed the registry (optionally hidden/read-only). The module-level stores skip their import-time hydration and derive persisted state from storage plus config alone, so a prefixed embed neither inherits bare-key state nor a previous mount's | Default prefix empty, `SAME_ORIGIN_INSTANCE` still seeded |
| 6 | Same-origin default instance (`baseUrl:""`) assumes the DB origin serves the SPA | `src/instances/registry.ts` (`SAME_ORIGIN_INSTANCE` / `configuredApiUrl`) | Default instance comes from `StudioConfig` (host passes its own base URL + credential); standalone default stays the config.js-seeded managed instance | Same-origin default unchanged |
| 7 | NL-assist backend choice is browser state a host cannot govern (instance-gateway default, browser-direct custom endpoints with a browser-held key - the mode model feature instance-config introduced) | `src/delegate/nl/config.ts` | `StudioConfig.nlAssist`: `disabled` \| `instance-only`, enforced at the transport choke point (`resolveNlConfig`, applied by `generateChat`), not just hidden in the UI | Absent = both modes with the instance default - current behavior |

A related small item folded in: the delegate-editor modal uses `Dialog.Portal` with no container
(`src/delegate/DelegateEditor.tsx`), so it escapes to `document.body` - fine standalone, wrong inside a
host region. It renders into the Studio root container instead.

> **Shared seam (feature [standalone-ui](../../done/standalone-ui/spec.md)):** coupling #6's "default instance
> from an external source" is the same registry config-injection seam that standalone-ui introduced
> (its producer is a runtime `config.js` setting `window.__F8_CONFIG__.apiUrl`). `StudioConfig.instances`
> is a second producer into that one seam, not a parallel path. Reuse it rather than re-designing it.

## Contract: `StudioConfig`

A single host-facing config object (all fields optional; omitting any reproduces standalone behavior):

```ts
interface StudioConfig {
  instances?: InstanceConfig[];        // host-supplied instances (default: [SAME_ORIGIN_INSTANCE])
  activeInstanceId?: string;
  lockInstances?: boolean;             // hide the connect/add UI when the host owns the instance
  namespace?: string;                  // seed the active namespace (default: "default")
  lockNamespace?: boolean;             // hide the namespace switcher when the embed is scoped to one graph
  basepath?: string;                   // router basepath (default "")
  history?: "browser" | "memory";      // "memory" keeps Studio out of the host's address bar (default "browser")
  storageNamespace?: string;           // prefix for localStorage keys (default "")
  theme?: Partial<ThemeTokens>;        // override surfaces, semantic accents, type (defaults: today's)
  queryClient?: QueryClient;           // reuse the host's client (default: Studio's own;
                                       // source-level embeds only - the artifact bundles
                                       // its own react-query copy)
  // "disabled" removes the NL panels; "instance-only" locks model calls to the active
  // instance's POST /chat. Structural: the browser-direct transports refuse under any
  // policy, and the NL store is policy-resolved at rehydrate (the persist merge in
  // delegate/nl/config.ts), so an instance-only embed neither holds nor re-persists a
  // custom-mode config or its third-party key:
  nlAssist?: "disabled" | "instance-only";
}

export function mountStudio(el: HTMLElement, config?: StudioConfig): { unmount(): void };
export function F8Studio(props: { config?: StudioConfig }): JSX.Element;
```

The frozen surface is whatever `src/embed/index.ts` re-exports: the two entry points above,
`F8GraphCanvas` (below) with its `F8GraphCanvasProps`, `DEFAULT_STYLE_CONFIG`, and the types a
host needs to satisfy them (`StudioConfig`, `ThemeTokens`, `InstanceConfig`, `InstanceAuth`,
`StyleConfig`, `CanvasNode`, `CanvasEdge`, `ElementRef`, `PathREST`).

**One live mount per page.** A second simultaneous mount throws: the two would share this
module's config, the instance registry singleton and the persisted keys, so the second would
silently rebind the first to its instances and credentials. Sequential mounts (unmount, then
mount with a new config) are the supported way to reconfigure, and each starts from storage
plus config alone rather than inheriting the previous mount's state.

### Host authentication: bearer tokens

The host signs its users in however it wants (typically provider-hosted OIDC); what reaches
Studio is a **per-instance data-plane credential**. The auth union gains a bearer arm whose
token comes from a host-supplied provider callback:

```ts
type InstanceAuth =
  | { kind: "none" }
  | { kind: "apiKey"; key: string; useBearer?: boolean; header?: string }
  | { kind: "bearer"; getToken(): Promise<string> };   // host-supplied, refresh is the host's job
```

The provider callback keeps token lifetime and refresh on the host's side and never exposes a
long-lived secret to Studio. A bearer instance is **managed, never persisted** (callbacks are not
serializable): it is supplied at mount time, exactly like the config.js-seeded default, and the
registry's persistence filter already excludes managed instances. The standalone kinds (`none`,
`apiKey`) and the same-origin default are unchanged.

Because the token only resolves asynchronously, the synchronous `authHeaders` **throws** for a
bearer instance rather than returning empty headers: a transport site that forgets
`resolveAuthHeaders` must fail loudly instead of silently sending an unauthenticated request. A
convention test additionally keeps `authHeaders` out of every call site but its own module. A
provider that rejects (expired session, revoked grant) fails the request rather than retrying,
including on the change-feed stream, so a dead token cannot become an endless callback loop.

### Namespace pinning

`StudioConfig.namespace` seeds the registry's per-instance active namespace for the
host-supplied instances; `lockNamespace` hides the switcher (and the namespace management and
recover-state switch) for embeds scoped to a single graph. Precedence: the pin seeds a
namespace nothing is remembered for, a remembered choice otherwise wins, and `lockNamespace`
forces the pin over a remembered one. Defaults (absent): the `default` namespace with the
switcher visible, exactly as today. The namespace-support probe (`GET /ns`) and the
pre-namespace degradation path keep working unchanged for host instances.

The locks are **UI affordances, not an authorization boundary**: Fallen-8 authenticates per
instance, not per namespace, so a credential that reaches one namespace reaches them all over
plain REST. Under browser history the namespace is in the URL and therefore user-editable; an
embed that wants the pin to hold in the UI should pair `lockNamespace` with
`history: "memory"`, and anything stronger belongs on the server.

### Theme tokens

The token surface is complete: background/panel/border/text colors, the semantic accents, and
the mono type stack (`fontMono`, the single `--font-mono` custom property Studio defines).
Today's values stay where Tailwind emits them (the `@theme` block, on `:root`) in the
standalone build; a host's `theme` overrides land as inline custom properties on the
`.f8-studio` root, which is what the utilities and primitives resolve through. Whether a host
reskins the embed to its own palette or keeps Studio's identity is the host's call; the seam
makes both possible with the same mechanism. (The library artifact re-emits every stylesheet
default under the `.f8-studio` scope root - the packaging build's scoping pass, see the plan -
so an embed never writes onto the host page's `:root`.)

### Canvas component export (in scope)

Alongside the whole app, the packaged surface exports the graph canvas as a standalone component
(working name `F8GraphCanvas`). The component boundary already exists:
`src/canvas/GraphCanvas.tsx` accepts `nodes`, `edges`, a `StyleConfig`, optional
path-overlay/emphasis/highlight, and an `onSelect` callback, and has no store or query
dependencies. The work is:

- freeze the prop contract (`CanvasNode`, `CanvasEdge`, `StyleConfig`, `ElementRef`) as public API,
  plus an optional `theme` so a host page can tint it without mounting the app,
- make its styles self-contained under the same `.f8-studio` scope,
- include it in the export surface next to `mountStudio`.

The internal `emphasis` prop (the adjacency-preview set the Canvas screen drives) is
deliberately **not** part of the frozen contract: it exists for Studio's own hover interaction
and would pin an implementation detail into the public API.

A host page can then render an interactive, styled graph from its own data (or data it fetched
from an instance) without mounting all of Studio.

## Non-goals (right-sizing - YAGNI until a real host exists)

- **No micro-frontend orchestration / module federation / runtime plugin system.** One export surface
  is enough. *Revisit trigger:* a second distinct host consumer.
- **No SSR / RSC.** *Revisit trigger:* a host that renders server-side.
- **No cookie-session data-plane auth.** Sending httpOnly session cookies to the data plane
  (`credentials: "include"` on every fetch and SSE call) is deliberately out: the bearer
  provider is the supported host path and keeps the transport layer credential-free. *Revisit
  trigger:* a host whose data plane cannot mint per-user bearer tokens.
- **No two live embeds on one page.** One mount per realm; a second concurrent mount fails
  loudly rather than silently cross-binding the two, through two complementary guards: the
  identity-based check in `setStudioConfig` (render-phase, tolerates StrictMode re-rendering
  the same config object) and the count-based check in `registerStudioMount`, which throws in
  the second tree's mount effect and therefore also catches two mounts SHARING one config
  object or landing in one commit. The second failure surfaces in React's effect phase, not
  at the `mountStudio` call site. Reconfiguring is unmount-then-mount: the config is read
  once per mount, so swapping `<F8Studio config>` in place does nothing, and a keyed
  same-commit remount is unsupported. *Revisit trigger for in-place reconfiguration:* a
  React host that needs live tenant switching without an unmount frame. Sequential mounts with
  different configs are supported and isolated: `storageNamespace` separates what each writes,
  and no mount inherits the previous one's in-memory state. *Revisit trigger:* a host that
  needs two live Studio embeds against different tenants on one page (it would need a realm per
  embed, e.g. an iframe, or the module state reworked into per-mount instances).
- **No host-supplied NL transport.** An earlier draft of this spec sketched
  `nlAssist: { transport }` (the host proxying model calls through its own backend). It was
  dropped when the contract was re-derived against the mode-based NL config that feature
  instance-config introduced meanwhile: instance mode already routes every model call through
  the host-controlled instance under the embed's own credential, so the arm would duplicate a
  server-side seam for no consumer. *Revisit trigger:* a host whose instances have no chat
  backend but who runs its own LLM gateway.

## Impact on existing features

The seam phases (1-5) were confined to `fallen-8-web-ui/` plus this feature's own spec and plan,
and every seam is inert under the default config, so what siblings saw was a code-contract
change, not a behavior change in the standalone app. The packaging phase (6) additionally
touched the CI workflow, the root README, the docs site and both architecture diagrams - the
discoverability gates it deliberately carried (see the last two bullets of "Explicitly not
affected", which flipped to "affected and done" with that phase).

- **[standalone-ui](../../done/standalone-ui/)** - its config.js seam generalizes: `managedInstances()`
  returns the host's `StudioConfig.instances` when there are any and the `configuredApiUrl()`-seeded
  same-origin default otherwise, and `isManagedInstance()` replaces the `id === "local"` test in the
  store's delete guard and in the Connect screen's Remove button. `"local"` stays reserved
  unconditionally, so a legacy whole-state blob cannot resurrect it as a personal instance under a host
  config. The partialize/merge contract is preserved (managed never persisted, personal instances and
  `activeNamespaces` untouched, `namespaceSupport` still dropped) with two additions: the persisted key
  runs through `storageKey()`, and `merge` derives every persisted field from storage plus config alone,
  because the store now skips its import-time hydration. Doc: the user-facing
  `docs/src/content/docs/standalone-ui.mdx` stays true (the managed default is still synthesized per
  load, un-removable and credential-free), so no edit there; the Remove-guard snippet quoted in
  `standalone-ui/spec.md` section 3 was corrected to the `isManagedInstance` form, with `registry.ts`
  carrying the living explanation.
- **[graph-namespaces](../../done/graph-namespaces/)** - the registry `merge` gains namespace precedence
  for managed instances (the `namespace` pin seeds an instance nothing is remembered for, a remembered
  choice wins otherwise, `lockNamespace` forces the pin), and the 404 recover state in `NamespaceScope`
  hides its switch-to-default button under `lockNamespace` while Recreate stays. Workspace store keys
  keep their `<instanceId>/<ns>` shape and only gain the prefix; the event-feed buffers are in-memory and
  untouched. Doc: its README's F8 Studio section describes the unlocked UI, which is unchanged, and the
  locks are explained once here, so no edit.
- **[web-ui](../../done/web-ui/)** - `InstanceAuth` gains the `bearer` arm; `authHeaders` throws for it
  and `resolveAuthHeaders` is the one function transport sites call (`apiRequest`, `apiForm`, the bulk
  export/import raw fetches of [bulk-import-export](../../done/bulk-import-export/), and the change-feed
  stream), with byte-identical headers for `none`/`apiKey`. `AppShell` renders a static label instead of
  the instance or namespace switcher under the locks; the Connect screen hides register/edit/remove,
  disables the activation radios and lists only managed instances under `lockInstances`, and stays
  reachable. The per-instance workspace stores take the storage prefix and are dropped from the memo map
  on every mount. Doc: `docs/src/content/docs/studio.md` describes the standalone Connect screen and top
  bar, both unchanged, so no edit. Screenshots: none affected, the phase 4 check recaptured
  `docs/src/assets/images/screen-connect.png` against a live apiApp and it matched the committed baseline
  except for the rendered date.
- **[change-feed](../../done/change-feed/)** - `streamChanges` resolves the credential before the connect
  attempt and closes `fatal` when a host token provider rejects, instead of retrying with backoff
  forever. `none`/`apiKey` never reach that path and `fatal` already meant "a non-retryable client error
  (400 bad filter, 401/403 auth)", so the standalone reconnect behavior is unchanged. Doc: no edit (its
  README owns the wire contract and the client recipe, not the SPA's close reasons).
- **[studio-first-run](../../done/studio-first-run/), [nl-assist-ux](../../done/nl-assist-ux/) and
  [instance-config](../../done/instance-config/)** - `f8.first-run` and `f8.nl-assist` route through
  `storageKey()`, skip their import-time hydration and gain a merge that derives their persisted fields
  from storage alone. The dismissal-per-`<instanceId>/<ns>` shape and the version-2 `builtin` to
  `instance` migration are untouched and the default prefix is empty, so the standalone stores behave
  exactly as before. The consequence a host must know: an embed with its own `storageNamespace` starts
  from the default NL-assist config (the browser-held `apiKey` included) and no dismissals, and neither
  inherits nor overwrites the standalone user's. Docs: no edit (all three describe what persists, not the
  key name).
- **Radix overlays across features** - the delegate editor, the Events panel
  ([studio-event-feed](../../done/studio-event-feed/)), the stored-query save dialog, the shared
  typed-name `ConfirmDialog`, the observability overlay and the first-run overlay now portal into the
  Studio root when a mount supplies one. Without a provider Radix still falls back to `document.body`, so
  standalone stacking and the single-home modal z-order in `index.css` are unchanged. Docs: no edit.

Explicitly **not** affected, verified against the diff:

- **Engine (`fallen-8-core`), the REST contract and the OpenAPI snapshot
  (`features/done/web-ui/openapi-v0.1.json`)**: no .NET file and no route changes, so the snapshot is not
  regenerated and the OpenAPI snapshot test stays green.
- **MCP (`fallen-8-mcp`)**: no new or changed REST operation, so the engine to REST to MCP propagation
  rule is not triggered and `McpRestCoverageTest`/`McpContractTest` stay green.
- **NL-assist fine-tune dataset and eval (`nl-assist-finetune/`)**: no `RETRAIN-LOG.md` entry. That log
  keys on the delegate-fragment surface the model drafts against, and no delegate kind, `type-model.json`
  entry, snippet or prompt changes here: `delegate/nl/prompt.ts` and `NlAssistPanel.tsx` are untouched and
  only `nl/config.ts`'s persistence plumbing moves.
- **Samples, stored queries and persisted recipes**: `samples/` is untouched, stored queries live
  server-side, and no persisted client payload shape changes, only the key name an embed writes under.
- **Compose and the deployables**: no Dockerfile or compose change - the artifact is a build
  output a host bundles, not a deployable this repo runs. CI changed with packaging, by design:
  the `ui` job builds the artifact (whose last step, `scripts/check-lib-artifact.mjs`, fails on
  an unscoped selector or a surviving `process.env` read), and the `e2e` job runs the bare-host
  smoke (`playwright.embed.config.ts` over `e2e-embed/`), so the artifact cannot silently rot.
  One knock-on: the all-in-one container smoke now curls `/F8Black.svg` instead of
  `/F8White.svg`, because the shell logo became a module asset (bundler-owned URL, inlined in
  the artifact) so an embed does not 404 it against the host origin.
- **Architecture diagrams (root `README.md` and
  [`architecture.md`](../../../docs/src/content/docs/architecture.md))**: both gained the
  embedded-Studio client shape (one `:::client` node, one `HTTP · CORS` edge into REST) when
  packaging landed, alongside the docs-site page (`embed-studio`, registered in the F8 Studio
  sidebar group) and the README key-features entry - the discoverability gates fired with the
  phase that made the surface consumable, exactly as planned.

## Behavior-preservation contract

Every phase lands with the standalone app's **full vitest suite green** and the default
`StudioConfig` reproducing today's behavior. A new test asserts "mount with no config == current
standalone bootstrap". Embeddability is strictly opt-in via config.
