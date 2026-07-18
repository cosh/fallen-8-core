# F8 Studio — embeddable in a host SaaS portal

Status: open (spec/plan only). Owner: TBD. Related: [web-ui](../../done/web-ui/),
[nl-assist-ux](../../done/nl-assist-ux/), [api-security-boundary](../../done/api-security-boundary/).

## Motivation

F8 Studio (`fallen-8-web-ui`) today is a standalone SPA served from the database's own
`apiApp/wwwroot` (`npm run build:apiapp`). A future SaaS portal wants to embed the Studio as a
**module inside its own shell** — its own routing, chrome, auth, and theme. The Studio is already
unusually well-positioned for this (see "Starting position"), but a handful of hard couplings would
make a drop-in embed collide with the host.

This feature adds the **seams** that make embedding possible. It is deliberately *additive*: the
standalone app must keep behaving exactly as it does today, and every seam defaults to the current
behavior. There is **no rewrite** and **no micro-frontend framework** here — just the minimum set of
injection points a host needs.

## Starting position (already good — do not regress)

- **One transport choke-point.** All server access goes through `apiRequest` in
  [`src/api/client.ts`](../../../fallen-8-web-ui/src/api/client.ts); base URL and auth headers are
  read per-instance (`instance.baseUrl`, `authHeaders(instance)`), never from a global constant.
- **Auth is an extensible union.** `InstanceConfig.auth` (`src/instances/types.ts`) is a discriminated
  union (`none | apiKey`) shaped so an OIDC/JWT variant slots in without touching call sites.
- **Server state is isolated** behind TanStack Query; client state behind Zustand.
- **`window`/`document` use is minimal** (canvas/anchor/setTimeout only) — not a coupling blocker.

## The seven couplings this feature removes

Each is a real, verified coupling (from the code-health-sweep recon). The **contract** column is the
observable behavior that must stay identical for the standalone app.

| # | Coupling | Where | Seam to add | Standalone contract |
|---|----------|-------|-------------|---------------------|
| 1 | App owns bootstrap (QueryClient, RouterProvider, StrictMode, `#root` mount) | `src/main.tsx` | Export `mountStudio(el, config)` / `<F8Studio config>`; `main.tsx` becomes a thin caller with default config | Same DOM, same providers, same defaults |
| 2 | App owns the URL at root paths | `src/app/routes.tsx` (`router`, no basepath) | Configurable router `basepath` (default `""`); optional memory-history mode | Root-path routes unchanged |
| 3 | Global, unscoped CSS (Tailwind preflight + `body` + generic `.btn/.panel/.input`) | `src/index.css` | Scope primitives + preflight under a `.f8-studio` root container / CSS layer; standalone wraps its root in the same scope | Pixel-identical standalone |
| 4 | Hard-coded dark theme (fixed hex `@theme` tokens + forced `html.dark`) | `src/index.css`, `index.html` | Theme tokens become CSS custom properties defaulting to today's palette; host may override; enables a future light theme | Same dark palette by default |
| 5 | Module singletons + fixed `localStorage` keys (`f8.instances`, `f8.workspace.<id>`, `f8.nl-assist`) with no host injection point | `src/instances/registry.ts`, `src/state/instanceStore.ts`, `src/delegate/nl/config.ts` | `StudioConfig` context supplying instance(s)/credentials and a storage-key namespace prefix; a host-supplied instance can seed the registry (optionally hidden/read-only) | Default prefix empty, `SAME_ORIGIN_INSTANCE` still seeded |
| 6 | Same-origin default instance (`baseUrl:""`) assumes the DB origin serves the SPA | `src/instances/registry.ts:26` | Default instance comes from `StudioConfig` (host passes its own base URL + token); standalone default stays `SAME_ORIGIN_INSTANCE` | Same-origin default unchanged |
| 7 | Browser-side LLM keys called directly from the browser | `src/delegate/nl/config.ts` | `StudioConfig.nlAssist`: `disabled` \| `direct` (today) \| host-supplied transport (proxy through the host backend) | Default `direct` — current behavior |

A related small item folded in: the delegate-editor modal uses `Dialog.Portal` with no container
(`src/delegate/DelegateEditor.tsx`), so it escapes to `document.body` — fine standalone, wrong inside a
host region. It renders into the Studio root container instead.

## Contract: `StudioConfig`

A single host-facing config object (all fields optional; omitting any reproduces standalone behavior):

```ts
interface StudioConfig {
  instances?: InstanceConfig[];        // host-supplied instances (default: [SAME_ORIGIN_INSTANCE])
  activeInstanceId?: string;
  lockInstances?: boolean;             // hide the connect/add UI when the host owns the instance
  basepath?: string;                   // router basepath (default "")
  storageNamespace?: string;           // prefix for localStorage keys (default "")
  theme?: Partial<ThemeTokens>;        // override the default dark tokens
  queryClient?: QueryClient;           // reuse the host's client (default: Studio's own)
  nlAssist?: "disabled" | "direct" | { transport: NlTransport };  // default "direct"
}

export function mountStudio(el: HTMLElement, config?: StudioConfig): { unmount(): void };
export function F8Studio(props: { config?: StudioConfig }): JSX.Element;
```

## Non-goals (right-sizing — YAGNI until a real host exists)

- **No micro-frontend orchestration / module federation / runtime plugin system.** One export surface
  is enough. *Revisit trigger:* a second distinct host consumer.
- **No SSR / RSC.** *Revisit trigger:* a host that renders server-side.
- **No cross-embed credential isolation beyond namespaced storage.** *Revisit trigger:* a host that
  runs two live Studio embeds against different tenants on one page.
- **No new build artifact until needed.** The vite library-mode build (Phase 5) ships only when a host
  actually consumes the package; until then the mount API exists but the standalone build is the only
  artifact produced by CI.

## Behavior-preservation contract

Every phase lands with the standalone app's existing **265 vitest tests green** and the default
`StudioConfig` reproducing today's behavior. A new test asserts "mount with no config == current
standalone bootstrap". Embeddability is strictly opt-in via config.
