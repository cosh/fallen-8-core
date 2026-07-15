import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
} from "@tanstack/react-router";
import { AppShell } from "./AppShell";
import { ConnectScreen } from "../screens/ConnectScreen";
import { DashboardScreen } from "../screens/DashboardScreen";
import { BrowserScreen } from "../screens/BrowserScreen";
import { QueryScreen } from "../screens/QueryScreen";
import { PathScreen } from "../screens/PathScreen";
import { SubgraphScreen } from "../screens/SubgraphScreen";
import { CanvasScreen } from "../screens/CanvasScreen";

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

const canvasRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/canvas",
  component: CanvasScreen,
});

const routeTree = rootRoute.addChildren([
  connectRoute,
  dashboardRoute,
  browserRoute,
  queryRoute,
  pathRoute,
  subgraphRoute,
  canvasRoute,
]);

export const router = createRouter({ routeTree });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
