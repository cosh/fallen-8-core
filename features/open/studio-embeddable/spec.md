# F8 Studio - embeddable in a host SaaS portal

Status: open (spec/plan only). Owner: TBD. Related: [web-ui](../../done/web-ui/),
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
| 5 | Module singletons + fixed `localStorage` keys (`f8.instances`, `f8.workspace.<id>`, `f8.nl-assist`) with no host injection point | `src/instances/registry.ts`, `src/state/instanceStore.ts`, `src/delegate/nl/config.ts` | `StudioConfig` context supplying instance(s)/credentials and a storage-key namespace prefix; a host-supplied instance can seed the registry (optionally hidden/read-only) | Default prefix empty, `SAME_ORIGIN_INSTANCE` still seeded |
| 6 | Same-origin default instance (`baseUrl:""`) assumes the DB origin serves the SPA | `src/instances/registry.ts` (`SAME_ORIGIN_INSTANCE` / `configuredApiUrl`) | Default instance comes from `StudioConfig` (host passes its own base URL + credential); standalone default stays the config.js-seeded managed instance | Same-origin default unchanged |
| 7 | Browser-side LLM keys called directly from the browser | `src/delegate/nl/config.ts` | `StudioConfig.nlAssist`: `disabled` \| `direct` (today) \| host-supplied transport (proxy through the host backend) | Default `direct` - current behavior |

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
  storageNamespace?: string;           // prefix for localStorage keys (default "")
  theme?: Partial<ThemeTokens>;        // override surfaces, semantic accents, type (defaults: today's)
  queryClient?: QueryClient;           // reuse the host's client (default: Studio's own)
  nlAssist?: "disabled" | "direct" | { transport: NlTransport };  // default "direct"
}

export function mountStudio(el: HTMLElement, config?: StudioConfig): { unmount(): void };
export function F8Studio(props: { config?: StudioConfig }): JSX.Element;
export { F8GraphCanvas };             // component-level export, see below
```

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

### Namespace pinning

`StudioConfig.namespace` seeds the registry's per-instance active namespace for the
host-supplied instance; `lockNamespace` hides the switcher for embeds scoped to a single graph.
Defaults (absent): the `default` namespace with the switcher visible, exactly as today. The
namespace-support probe (`GET /ns`) and the pre-namespace degradation path keep working
unchanged for host instances.

### Theme tokens

The token surface is complete: background/panel/border/text colors, the semantic accents, and
the type stack (the `--font-*` custom properties). All become CSS custom properties on the
`.f8-studio` root, defaulting to today's values. Whether a host reskins the embed to its own
palette or keeps Studio's identity is the host's call; the seam makes both possible with the
same mechanism.

### Canvas component export (in scope)

Alongside the whole app, the packaged surface exports the graph canvas as a standalone component
(working name `F8GraphCanvas`). The component boundary already exists:
`src/canvas/GraphCanvas.tsx` accepts `nodes`, `edges`, a `StyleConfig`, optional
path-overlay/emphasis/highlight, and an `onSelect` callback, and has no store or query
dependencies. The work is:

- freeze the prop contract (`CanvasNode`, `CanvasEdge`, `StyleConfig`, `ElementRef`) as public API,
- make its styles self-contained under the same `.f8-studio` scope,
- include it in the export surface next to `mountStudio`.

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
- **No cross-embed credential isolation beyond namespaced storage.** *Revisit trigger:* a host that
  runs two live Studio embeds against different tenants on one page.
- **No new build artifact until needed.** The vite library-mode build (packaging phase) ships only
  when a host actually consumes the package; until then the mount API exists but the standalone
  build is the only artifact produced by CI.

## Behavior-preservation contract

Every phase lands with the standalone app's **full vitest suite green** and the default
`StudioConfig` reproducing today's behavior. A new test asserts "mount with no config == current
standalone bootstrap". Embeddability is strictly opt-in via config.
