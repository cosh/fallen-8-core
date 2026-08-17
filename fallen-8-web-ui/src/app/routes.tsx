// MIT License
//
// routes.tsx
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
  Outlet,
} from "@tanstack/react-router";
import type { StudioConfig } from "./studioConfig";
import { AppShell } from "./AppShell";
import { NamespaceScope } from "./NamespaceScope";
import { useRegistry, DEFAULT_NAMESPACE } from "../instances/registry";
import { ConnectScreen } from "../screens/ConnectScreen";
import { DashboardScreen } from "../screens/DashboardScreen";
import { SamplesScreen } from "../screens/SamplesScreen";
import { SaveGamesScreen } from "../screens/SaveGamesScreen";
import { BrowserScreen } from "../screens/BrowserScreen";
import { QueryScreen } from "../screens/QueryScreen";
import { IndexesScreen } from "../screens/IndexesScreen";
import { PathScreen } from "../screens/PathScreen";
import { SubgraphScreen } from "../screens/SubgraphScreen";
import { AnalyticsScreen } from "../screens/AnalyticsScreen";
import { PluginsScreen } from "../screens/PluginsScreen";
import { CanvasScreen } from "../screens/CanvasScreen";
import { BenchmarkScreen } from "../screens/BenchmarkScreen";
import { KnowledgeScreen } from "../screens/KnowledgeScreen";
import { IntegrationsScreen } from "../screens/IntegrationsScreen";

const rootRoute = createRootRoute({
  component: () => (
    <AppShell>
      <Outlet />
    </AppShell>
  ),
});

const connectRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: ConnectScreen,
});

// NOTE: "/save-games" (hyphen) - the un-hyphenated path is the real GET /savegames API
// route, which would win over the SPA fallback on a full-page load (same reason /subgraphs
// is plural). Save games are Fallen-8-level (entries can span namespaces), so the route
// stays OUTSIDE /q/$ns - like Connect and Integrations.
const saveGamesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/save-games",
  component: SaveGamesScreen,
});

/**
 * Namespace-scoped screens live under /q/{ns}/… (feature graph-namespaces): the namespace
 * is IN the app URL, so a pasted link restores it. "/q" collides with no API route, so the
 * SPA fallback serves full-page loads. NamespaceScope syncs the param into the registry and
 * renders the recover state for unknown namespaces.
 */
const namespaceRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/q/$ns",
  component: NamespaceScope,
});

const dashboardRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "dashboard",
  component: DashboardScreen,
});

// Sample-graph gallery (feature sample-graphs): its own screen so the gallery gets full
// width and a tag filter. Namespace-scoped - loading a sample writes into the active graph.
const samplesRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "samples",
  component: SamplesScreen,
});

const browserRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "browser",
  component: BrowserScreen,
});

// The semantic-layer screen (feature semantic-layer, renamed from "documents"). "knowledge"
// avoids the singular /document API route the SPA fallback would otherwise lose to. Namespace-
// scoped: ingestion writes into the active graph.
const knowledgeRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "knowledge",
  component: KnowledgeScreen,
});

// Continuity for bookmarks to the old /q/{ns}/documents URL: redirect to /q/{ns}/knowledge.
const knowledgeLegacyRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "documents",
  beforeLoad: ({ params }) => {
    throw redirect({ to: "/q/$ns/knowledge", params: { ns: (params as { ns: string }).ns } });
  },
});

const queryRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "query",
  component: QueryScreen,
});

// NOTE: "indexes" (plural) - the singular path is the real POST /index API route (same
// reason "subgraphs" is plural below; kept although /q/… never collides, for consistency).
const indexesRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "indexes",
  component: IndexesScreen,
});

const pathRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "path",
  component: PathScreen,
});

const subgraphRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "subgraphs",
  component: SubgraphScreen,
});

const analyticsRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "analytics",
  component: AnalyticsScreen,
});

// Plugins (feature plugin-registration): the built-in plugin families plus the namespace's
// runtime-authored, compile-validated registry. Namespace-scoped (registrations are per graph).
const pluginsRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "plugins",
  component: PluginsScreen,
});

const canvasRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "canvas",
  component: CanvasScreen,
});

// NOTE: "benchmarks" (plural) - the singular leaf would still be fine under /q/{ns}/, but the
// plural keeps the URL a bookmark redirect away from the flat /benchmarks it replaced (same
// reason "indexes" and "subgraphs" are plural). Namespace-scoped: generation writes the active
// graph and the benchmark measures it.
const benchmarkRoute = createRoute({
  getParentRoute: () => namespaceRoute,
  path: "benchmarks",
  component: BenchmarkScreen,
});

/** The active namespace read OUTSIDE React (redirects run before any component mounts). */
function activeNamespace(): string {
  const s = useRegistry.getState();
  return (s.activeId && s.activeNamespaces[s.activeId]) || DEFAULT_NAMESPACE;
}

/**
 * Pre-namespace bookmarks (/dashboard, /canvas, …) redirect to the active namespace's
 * equivalent, so old links keep working. The screens added AFTER the namespace migration
 * (Samples, Plugins) are intentionally absent: they never had pre-namespace bookmarks, and
 * a bare /plugins would in any case resolve to the REST /plugins route (not the SPA) on a
 * full-page load — both are reached via the rail, which links the scoped /q/{ns}/… URL.
 *
 * "/benchmarks" joined the list when Benchmark stopped being Fallen-8-level: it was a flat
 * route for real, so links to it exist and must land on the active namespace's screen.
 */
const LEGACY_SCOPED_PATHS = [
  "/dashboard",
  "/browser",
  "/query",
  "/indexes",
  "/path",
  "/subgraphs",
  "/analytics",
  "/canvas",
  "/benchmarks",
] as const;

const legacyRedirectRoutes = LEGACY_SCOPED_PATHS.map((path) =>
  createRoute({
    getParentRoute: () => rootRoute,
    path,
    beforeLoad: () => {
      throw redirect({ to: `/q/$ns${path}`, params: { ns: activeNamespace() } });
    },
  }),
);

// Integrations are Fallen-8-level: one runtime serves the whole instance and a job names the
// namespace it writes into, so the route stays OUTSIDE /q/$ns - like Save games and Connect.
const integrationsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/integrations",
  component: IntegrationsScreen,
});

const routeTree = rootRoute.addChildren([
  connectRoute,
  saveGamesRoute,
  integrationsRoute,
  namespaceRoute.addChildren([
    dashboardRoute,
    samplesRoute,
    browserRoute,
    knowledgeRoute,
    knowledgeLegacyRoute,
    queryRoute,
    indexesRoute,
    pathRoute,
    subgraphRoute,
    analyticsRoute,
    pluginsRoute,
    canvasRoute,
    benchmarkRoute,
  ]),
  ...legacyRedirectRoutes,
]);

/**
 * The router, parameterized by the embed seams (feature studio-embeddable): `basepath`
 * mounts every route under the host's prefix (default "": root, as standalone), and
 * `history: "memory"` keeps Studio out of the address bar when the host owns the URL.
 */
export function createStudioRouter(
  config: Pick<StudioConfig, "basepath" | "history"> = {},
) {
  return createRouter({
    routeTree,
    basepath: config.basepath ?? "",
    history: config.history === "memory" ? createMemoryHistory() : undefined,
  });
}

/** The standalone router: root basepath, browser history - exactly the pre-seam behavior. */
export const router = createStudioRouter();

// The TanStack `Register` augmentation lives in src/types/router-register.d.ts, NOT here: an
// augmentation in a .ts source would ride the emitted declarations into the library artifact
// and hijack the router types of a host that registers its own TanStack router. A .d.ts input
// is consumed by every in-repo typecheck but never re-emitted by the lib build.
