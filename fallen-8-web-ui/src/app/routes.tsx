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
import { SaveGamesScreen } from "../screens/SaveGamesScreen";
import { BrowserScreen } from "../screens/BrowserScreen";
import { QueryScreen } from "../screens/QueryScreen";
import { IndexesScreen } from "../screens/IndexesScreen";
import { PathScreen } from "../screens/PathScreen";
import { SubgraphScreen } from "../screens/SubgraphScreen";
import { AnalyticsScreen } from "../screens/AnalyticsScreen";
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
 * equivalent, so old links keep working.
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
    browserRoute,
    queryRoute,
    indexesRoute,
    pathRoute,
    subgraphRoute,
    analyticsRoute,
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
