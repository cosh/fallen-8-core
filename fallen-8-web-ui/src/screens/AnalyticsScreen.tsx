import { AnalyticsRunner } from "../components/AnalyticsRunner";
import { GraphShapePanel } from "../components/GraphShapePanel";

/**
 * Analytics (feature studio-coverage §3/§4): understand the graph's shape, then compute
 * over it. The Graph shape panel is the ONLY caller of GET /statistics (on demand — the
 * pass is budgeted and rate-limited); its snapshot doubles as the schema cache feeding
 * identifier suggestions across the Studio (gap G-3). The runner mirrors the backend's
 * one-shot design: no history, no queueing — 429/408 are first-class outcomes.
 */
export function AnalyticsScreen() {
  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <GraphShapePanel />
      <AnalyticsRunner />
    </div>
  );
}
