import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
} from "@tanstack/react-router";
import { AppShell } from "./AppShell";
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

const dashboardRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/dashboard",
  component: DashboardScreen,
});

// NOTE: "/save-games" (hyphen) - the un-hyphenated path is the real GET /savegames API
// route, which would win over the SPA fallback on a full-page load (same reason /subgraphs
// is plural).
const saveGamesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/save-games",
  component: SaveGamesScreen,
});

const browserRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/browser",
  component: BrowserScreen,
});

const queryRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/query",
  component: QueryScreen,
});

// NOTE: "/indexes" (plural) - the singular path is the real POST /index API route, which
// would win over the SPA fallback on a full-page load (same reason /subgraphs is plural).
const indexesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/indexes",
  component: IndexesScreen,
});

const pathRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/path",
  component: PathScreen,
});

// NOTE: "/subgraphs" (plural) - the singular path is the real GET /subgraph API route,
// which would win over the SPA fallback on a full-page load.
const subgraphRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/subgraphs",
  component: SubgraphScreen,
});

// NOTE: bare "/analytics" is safe - the API only has /analytics/algorithms and
// /analytics/{name} (both deeper), so the SPA fallback wins on a full-page load.
const analyticsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/analytics",
  component: AnalyticsScreen,
});

const canvasRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/canvas",
  component: CanvasScreen,
});

// NOTE: "/benchmarks" (plural) - the singular path is the real GET /benchmark API route,
// which would win over the SPA fallback on a full-page load (same reason /subgraphs is
// plural).
const benchmarkRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/benchmarks",
  component: BenchmarkScreen,
});

const routeTree = rootRoute.addChildren([
  connectRoute,
  dashboardRoute,
  saveGamesRoute,
  browserRoute,
  queryRoute,
  indexesRoute,
  pathRoute,
  subgraphRoute,
  analyticsRoute,
  canvasRoute,
  benchmarkRoute,
]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
