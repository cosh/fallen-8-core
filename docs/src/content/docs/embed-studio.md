---
title: "Embed F8 Studio"
description: "Mount F8 Studio inside a host application's own shell: the library artifact, mountStudio and StudioConfig, bearer auth, namespace pinning, theme tokens, and the standalone graph canvas component."
---

There are two ways to put F8 Studio in front of your users. The first needs no code at all:
deploy the [standalone container](/fallen-8-core/standalone-ui/) at its own origin and link to
it, with a runtime `config.js` pointing it at the right instance. The second is this page:
your application (a host portal, an internal tool, an admin console) renders Studio **inside
its own shell** - its routing, its auth, its chrome - through a library artifact and one
config object. Everything here is opt-in: every `StudioConfig` field has a default that
reproduces the standalone app exactly.

## The artifact

The library build lives in the
[`fallen-8-web-ui`](https://github.com/cosh/fallen-8-core/tree/main/fallen-8-web-ui) package:

```bash
npm run build:lib   # in fallen-8-web-ui/
```

It produces `dist-lib/`: one ES module (the export surface of
[`src/embed/index.ts`](https://github.com/cosh/fallen-8-core/blob/main/fallen-8-web-ui/src/embed/index.ts)),
one stylesheet, and TypeScript declarations, wired through the package's `exports` map. The
package declares `react` and `react-dom` (19+) as peer dependencies: your application brings
its own React and bundles the artifact like any dependency (a bundler is required; the module
is not served raw). Consume it as a `file:`/workspace dependency or as a packed tarball
(`npm pack` after `build:lib`); the package is deliberately not published to a registry.

Two imports, one call:

```ts
import { mountStudio } from "fallen-8-web-ui";
import "fallen-8-web-ui/styles.css"; // the stylesheet is NOT injected by the module

const studio = mountStudio(document.getElementById("studio")!, {
  instances: [{
    id: "tenant-graph",
    name: "Tenant graph",
    baseUrl: "https://f8.example.internal",
    auth: { kind: "bearer", getToken: () => myAuth.freshToken() },
  }],
  lockInstances: true,
  namespace: "default",
  lockNamespace: true,
  history: "memory",
  storageNamespace: "tenant-42.",
  nlAssist: "instance-only",
});
// later: studio.unmount()
```

React hosts render `<F8Studio config={...} />` instead; same contract, no imperative handle.

## The `StudioConfig` contract

Every field is optional; omitting all of them is exactly the standalone app.

| Field | What it does | Default |
| --- | --- | --- |
| `instances` | The instances Studio offers, supplied by the host. Host-supplied instances are managed: never persisted, re-created on every mount | the same-origin instance |
| `activeInstanceId` | Which instance starts active | the first |
| `lockInstances` | Hides register/edit/remove and the activation radios; the shell shows a static label | `false` |
| `namespace` | Seeds the active namespace when nothing is remembered for the instance | `default` |
| `lockNamespace` | Hides the namespace switcher and management; forces the pin over a remembered choice | `false` |
| `basepath` | Router prefix when Studio lives under a host route | `""` |
| `history` | `"memory"` keeps Studio's navigation out of the host's address bar | `"browser"` |
| `storageNamespace` | Prefix for every `localStorage` key, so embeds and the standalone app never share state | `""` |
| `theme` | Token overrides (surfaces, accents, the mono font stack); anything omitted keeps Studio's dark defaults | Studio's palette |
| `queryClient` | Reuse the host's TanStack QueryClient. Source-level embedding only: the packaged artifact bundles its own `@tanstack/react-query` copy, so a host client from another copy only half-works (focus/online managers diverge) - leave it unset when consuming the artifact | Studio's own |
| `nlAssist` | `"disabled"` removes the NL-assist panels; `"instance-only"` locks model calls to the instance's `POST /chat`. Enforced structurally: the browser-direct transports refuse under any policy, and the NL store is policy-resolved at rehydrate, so an instance-only embed neither holds nor re-persists a custom endpoint config or its third-party key | standalone behavior |

**One live mount per page.** A second simultaneous mount fails loudly (the second tree's
mount errors rather than silently rebinding the first to its config): two embeds would share
one instance registry and one set of persisted keys. Reconfiguring means unmount, then mount
with the new config - each mount starts from storage plus config alone. The config is read
once per mount, so swapping the `config` prop of a live `<F8Studio>` in place does nothing,
and remounting it with a different config inside the same React commit is unsupported;
unmount, let React commit the removal, then mount.

### Bearer tokens

The `bearer` auth arm is how a host hands Studio a per-user, per-instance credential without
ever exposing a long-lived secret:

```ts
auth: { kind: "bearer", getToken: () => Promise<string> }
```

The token is resolved per request (the change-feed stream included), refresh stays the host's
job, and a rejecting provider fails the request rather than retrying forever. Bearer instances
are never persisted, because a callback cannot be. The standalone arms (`none`, `apiKey`) are
unchanged.

The embed calls the REST API cross-origin exactly like the standalone container, so the data
plane's `AllowedCorsOrigins` must include the host's origin, and the usual
[security rules](/fallen-8-core/security/) apply unchanged.

### What the locks are, honestly

`lockInstances` and `lockNamespace` are **UI affordances, not an authorization boundary**.
Fallen-8 authenticates per instance, not per namespace: a credential that reaches one
namespace reaches them all over plain REST. Under browser history the namespace is in the URL
and user-editable, so pair `lockNamespace` with `history: "memory"` if the pin should hold in
the UI - and put anything stronger on the server.

## Styling and theming

Everything Studio styles lives under one `.f8-studio` scope root, and the library stylesheet
ships with its reset scoped the same way, so the artifact neither styles the host page (no
bare `html`, `body` or `:root` rule survives the build; a check fails the build otherwise) nor
depends on the host loading a reset. `theme` overrides land as inline custom properties on the
scope root and win over the stylesheet defaults.

One boundary to know: Studio's styles sit in CSS cascade layers, and **unlayered host CSS
beats layered CSS** regardless of specificity. Scoping stops Studio leaking out; it cannot
stop an aggressive unlayered host reset (say, a global `button { all: unset }`) leaking in.
Keep global resets layered, or away from the region that hosts the embed.

## The graph canvas alone

Hosts that want an interactive graph without all of Studio import the canvas as a component:

```tsx
import { F8GraphCanvas } from "fallen-8-web-ui";
import "fallen-8-web-ui/styles.css";

<F8GraphCanvas
  nodes={{ 1: { id: 1, label: "turbine" }, 2: { id: 2, label: "site" } }}
  edges={{ 10: { id: 10, source: 1, target: 2, edgePropertyId: "locatedAt", label: null } }}
  onSelect={(ref) => console.log(ref)}
  theme={{ accent: "#e2001a" }}
/>
```

It renders in its own `.f8-studio` scope (no app shell required), takes the same style config
the Studio canvas uses, and reports selections through `onSelect`. Size it via its parent; it
fills what it is given.

## Boundaries

- **A bundler is required.** React is external; the module expects the host's build to resolve
  peers and asset imports.
- **The Samples gallery reads its datasets from this repository's public mirror** in an embed
  (the host origin does not serve `/samples`); everything else talks only to the configured
  instance.
- **The code editor's worker is inlined** in the artifact, so no worker file needs hosting and
  no extra CSP entry for a worker URL is needed beyond `blob:` workers.
- **Verified end to end in CI**: a bare host application consumes the built package (exports
  map, peer resolution, scoped styles, editor, canvas, unmount) on every push.

How the embed fits the topology is on the [architecture page](/fallen-8-core/architecture/);
the Studio feature set itself is documented at [F8 Studio](/fallen-8-core/studio/) and applies
unchanged inside an embed.
