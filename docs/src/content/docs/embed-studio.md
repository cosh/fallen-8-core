---
title: "Embed F8 Studio"
description: "Mount F8 Studio inside a host application's own shell with the @fallen-8/studio package: mountStudio and StudioConfig, bearer auth, namespace pinning, theme tokens, and the graph canvas on its own with host-settable sizes and camera control."
---

There are two ways to put F8 Studio in front of your users. The first needs no code at all:
deploy the [standalone container](/standalone-ui/) at its own origin and link to
it, with a runtime `config.js` pointing it at the right instance. The second is this page:
your application (a host portal, an internal tool, an admin console) renders Studio **inside
its own shell** - its routing, its auth, its chrome - through one package and one
config object. Everything here is opt-in: every `StudioConfig` field has a default that
reproduces the standalone app exactly. (This page is the embed CONTRACT; the staged journey -
an in-browser WASM engine, the canvas over it, then the full Studio - is walked end to end in
[Embed scenarios](/embed-scenarios/).)

## The package

```bash
npm install @fallen-8/studio
```

`@fallen-8/studio` is built from
[`fallen-8-web-ui`](https://github.com/cosh/fallen-8-core/tree/main/fallen-8-web-ui) and published
by the release workflow on a version tag, with a provenance attestation.

`react` and `react-dom` (19+) are its only dependencies of any kind, declared as peers: your
application brings its own React, and the artifact carries everything else (sigma, graphology,
three, and the editor) inside itself. So installing it pulls nothing but the tarball, and there is
no dependency list to keep in step with yours. A bundler is required; the module is not served raw.

:::note
Registry publishing is wired but not yet switched on: the release job skips it until the npm
credential is configured, so `npm install @fallen-8/studio` will not resolve before the first
release that runs with it. Until then, build the artifact and consume it locally as shown below.
:::

Two entry points, because the graph is worth much less than the app shell it used to arrive
with:

| Import | You get | You pay for |
| --- | --- | --- |
| `@fallen-8/studio` | `mountStudio`, `F8Studio`, and the canvas | the whole shell, including the code editor |
| `@fallen-8/studio/canvas` | the canvas surface only | the graph renderers and nothing else |

Both resolve through the package's `exports` map to prebuilt ES modules plus TypeScript
declarations. A build check fails the release if the editor ever leaks into the canvas entry's
chunk graph, so the second row stays true rather than merely intended.

To develop against an unreleased change, build the artifact and consume it as a
`file:`/workspace dependency or a packed tarball:

```bash
npm run build:lib   # in fallen-8-web-ui/, emits dist-lib/
npm pack
```

Two imports, one call:

```ts
import { mountStudio } from "@fallen-8/studio";
import "@fallen-8/studio/styles.css"; // the stylesheet is NOT injected by the module

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
[security rules](/security/) apply unchanged.

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
import { F8GraphCanvas } from "@fallen-8/studio/canvas";
import "@fallen-8/studio/styles.css";

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

### Sizing it for the box you have

Graph renderers measure in absolute pixels, so a node radius that reads well in a 1440 px card is
a hairline in a 3840 px one: the graph shrinks, relatively, as the container grows. Your page is
the only party that knows how big its box is, so `StyleConfig` takes the magnitudes as numbers:

| Field | What it sets | Default |
| --- | --- | --- |
| `nodeSize` | node radius in px, for `nodeSizeMode: "fixed"`, and what the scaled modes draw for a node they cannot measure | `NODE_SIZE_DEFAULT`, 5 |
| `nodeSizeRange` | `[min, max]` node radius in px for the scaled modes | `NODE_SIZE_RANGE`, `[3, 14]` |
| `edgeWidth` | edge width in px, for `edgeWidthMode: "fixed"` | `EDGE_WIDTH_DEFAULT`, 1 |
| `edgeWidthRange` | `[min, max]` edge width in px | `EDGE_WIDTH_RANGE`, `[0.5, 5]` |
| `labelSize` | node label px | `LABEL_SIZE_DEFAULT`, 11 |
| `edgeLabelSize` | edge label px | `EDGE_LABEL_SIZE_DEFAULT`, 9 |

Every one is optional, and omitting all of them renders exactly as before they existed, which is
what keeps `F8GraphCanvasProps` a frozen contract. One nuance worth knowing: if you set a range and
leave the matching scalar alone, the "cannot measure" fallback follows your range rather than the
stock default, so those elements stay on the scale you asked for. Name the scalar and it is used
verbatim. The defaults are exported as the constants
named above, so you can scale FROM them rather than guessing:

```tsx
import {
  DEFAULT_STYLE_CONFIG,
  F8GraphCanvas,
  LABEL_SIZE_DEFAULT,
  NODE_SIZE_DEFAULT,
} from "@fallen-8/studio/canvas";

// Your policy, your numbers. Nothing here happens implicitly.
const scale = Math.max(1, containerWidth / 1440);

<F8GraphCanvas
  nodes={nodes}
  edges={edges}
  config={{
    ...DEFAULT_STYLE_CONFIG,
    nodeSize: NODE_SIZE_DEFAULT * scale,
    labelSize: LABEL_SIZE_DEFAULT * scale,
  }}
/>
```

Two deliberate boundaries. Sizes never scale themselves with the viewport or the device pixel
ratio: you get the knobs and decide, so the same config always renders the same picture. And the
path-overlay minimums still win, so a highlighted path cannot be shrunk into invisibility however
small you set the rest.

### Driving the camera

`F8GraphCanvas` takes a `ref` exposing three methods, implemented by both the 2D and 3D renderers:

```tsx
import { useRef } from "react";
import { F8GraphCanvas, type F8GraphCanvasHandle } from "@fallen-8/studio/canvas";

const canvas = useRef<F8GraphCanvasHandle>(null);

<F8GraphCanvas ref={canvas} nodes={nodes} edges={edges} />;

canvas.current?.fitToView();            // frame everything, renderer's own inset
canvas.current?.fitToView(300, 80);     // over 300 ms, with 80 px of margin
canvas.current?.getCameraRatio();       // 1 means "the graph just fits"
canvas.current?.setCameraRatio(0.5);    // twice as close
```

`ratio` means the same thing in both renderers and is derived on every call, so 1 still means
"fits" after the graph grows or the container changes. Two things to know before you compute with
it. Asking for a margin is a zoom, so it scales element sizes the way any zoom does (pad the
container instead if you want margin without one). And zooming is not a uniform scale: multiplying
the ratio by k spreads the layout over 1/k of the distance but draws each element at 1/sqrt(k) of
its size, because the renderer scales lengths by the square root of the ratio. Labels do not scale
with it at all. In 2D any finite positive ratio is applied verbatim, so a slider needs your own
bounds; `fitToView()` is always the way back.

The canvas keeps itself framed when its box changes, without ever overriding a view you or your
visitor set: the 2D renderer re-measures and repaints without touching the camera at all, and the
3D renderer re-fits only until the first time someone moves it with the mouse.

## Boundaries

- **A bundler is required.** React is external; the module expects the host's build to resolve
  peers and asset imports.
- **One stylesheet, whichever entry you import.** `@fallen-8/studio/styles.css` is the whole
  Studio stylesheet (scoped, so it styles only the embed). The canvas entry splits the
  JavaScript, not the CSS; a canvas-only host loads rules it does not use.
- **The Samples gallery reads its datasets from this repository's public mirror** in an embed
  (the host origin does not serve `/samples`); everything else talks only to the configured
  instance.
- **The code editor's worker is inlined** in the artifact, so no worker file needs hosting and
  no extra CSP entry for a worker URL is needed beyond `blob:` workers.
- **Verified end to end in CI**: a bare host application consumes the built package (exports
  map, peer resolution, scoped styles, editor, canvas, unmount) on every push.

How the embed fits the topology is on the [architecture page](/architecture/);
the Studio feature set itself is documented at [F8 Studio](/studio/) and applies
unchanged inside an embed.
