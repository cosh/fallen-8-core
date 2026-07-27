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
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
  Outlet,
} from "@tanstack/react-router";
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
// stays OUTSIDE /q/$ns - like /benchmarks and Connect.
const saveGamesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/save-games",
  component: SaveGamesScreen,
});

// NOTE: "/benchmarks" (plural) - the singular path is the real GET /benchmark API route,
// which would win over the SPA fallback on a full-page load. Benchmark is Fallen-8-level.
const benchmarkRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/benchmarks",
  component: BenchmarkScreen,
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

const routeTree = rootRoute.addChildren([
  connectRoute,
  saveGamesRoute,
  benchmarkRoute,
  namespaceRoute.addChildren([
    dashboardRoute,
    samplesRoute,
    browserRoute,
    queryRoute,
    indexesRoute,
    pathRoute,
    subgraphRoute,
    analyticsRoute,
    pluginsRoute,
    canvasRoute,
  ]),
  ...legacyRedirectRoutes,
]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
