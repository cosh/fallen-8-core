import { useNavigate } from "@tanstack/react-router";
import { useActiveInstance } from "../instances/registry";
import { useGraphShape } from "../state/graphShape";
import { getInstanceStore } from "../state/instanceStore";
import type {
  CardinalityStatsREST,
  DegreeStatsREST,
  GraphStatisticsREST,
} from "../api/types";
import { Stat } from "../components/Stat";
import { ErrorBox } from "../components/ErrorBox";

/**
 * Analytics (feature studio-coverage §3/§4): understand the graph's shape, then compute
 * over it. The Graph shape panel is the ONLY caller of GET /statistics (on demand — the
 * pass is budgeted and rate-limited); its snapshot doubles as the schema cache feeding
 * identifier suggestions across the Studio (gap G-3). The algorithm runner lands below
 * it (concept spec §3.2/3.3).
 */
export function AnalyticsScreen() {
  return (
    <div className="mx-auto max-w-5xl space-y-4">
      <GraphShapePanel />
    </div>
  );
}

function CardinalityColumn({
  title,
  stats,
}: {
  title: string;
  stats: CardinalityStatsREST | undefined;
}) {
  const top = stats?.top ?? [];
  return (
    <div className="panel">
      <div className="panel-title">
        {title}
        <span className="text-fg-faint normal-case">
          {stats ? `${stats.distinctTotal} distinct` : ""}
        </span>
      </div>
      <ul className="p-3 text-[12px]">
        {top.length === 0 && <li className="text-fg-faint">none</li>}
        {top.map((entry) => (
          <li
            key={entry.name ?? "—"}
            className="text-fg-dim flex justify-between gap-2"
          >
            <span className="truncate">{entry.name ?? "—"}</span>
            <span className="text-fg-faint">{entry.count.toLocaleString()}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

const DEGREE_COLUMNS = ["min", "mean", "p50", "p90", "p99", "max"] as const;

function degreeCell(stats: DegreeStatsREST, column: (typeof DEGREE_COLUMNS)[number]) {
  const value = stats[column];
  return column === "mean" ? value.toFixed(1) : value.toLocaleString();
}

function DegreeTable({ shape }: { shape: GraphStatisticsREST }) {
  const rows: [string, DegreeStatsREST][] = [
    ["in", shape.inDegree],
    ["out", shape.outDegree],
    ["total", shape.totalDegree],
  ];
  return (
    <div className="panel overflow-x-auto">
      <div className="panel-title">degrees</div>
      <table className="w-full font-mono text-[12px]">
        <thead>
          <tr className="text-fg-faint">
            <th className="table-cell" />
            {DEGREE_COLUMNS.map((column) => (
              <th key={column} className="table-cell text-right">
                {column}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map(([name, stats]) => (
            <tr key={name}>
              <td className="table-cell text-fg-faint">{name}</td>
              {DEGREE_COLUMNS.map((column) => (
                <td key={column} className="table-cell text-fg-dim text-right">
                  {degreeCell(stats, column)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function GraphShapePanel() {
  const instance = useActiveInstance()!;
  const shape = useGraphShape(instance);
  const store = getInstanceStore(instance.id);
  const setScanPrefill = store((s) => s.setScanPrefill);
  const navigate = useNavigate();
  const data = shape.data;

  return (
    <section className="panel">
      <div className="panel-title">
        Graph shape
        {data && (
          <span className="text-fg-faint normal-case">
            computed in {data.computedInMs.toFixed(0)} ms
          </span>
        )}
        {data?.sampled && (
          <span
            className="text-warn normal-case"
            data-testid="shape-sampled"
            title="per-name counts and distinct totals are within-sample — multiply counts by the stride to extrapolate"
          >
            sampled 1:{data.sampleStride}
          </span>
        )}
        <button
          type="button"
          className="btn btn-accent ml-auto"
          data-testid="shape-compute"
          disabled={shape.isFetching}
          onClick={() => shape.refetch()}
        >
          {shape.isFetching ? "Computing…" : data ? "Recompute" : "Compute"}
        </button>
      </div>
      <p className="text-fg-faint px-3 pt-2 text-[11px]">
        Full O(V+E) pass, sampled above the configured element budget — computed only on
        demand. The snapshot also feeds identifier suggestions on the Query screen.
      </p>

      {shape.isError && (
        <div className="p-3">
          <ErrorBox error={shape.error} onRetry={() => shape.refetch()} />
        </div>
      )}

      {data && (
        <div className="space-y-3 p-3" data-testid="shape-result">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-2">
            <Stat label="vertices" value={data.vertexCount.toLocaleString()} />
            <Stat label="edges" value={data.edgeCount.toLocaleString()} />
          </div>

          <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
            <CardinalityColumn title="vertex labels" stats={data.vertexLabels} />
            <CardinalityColumn title="edge labels" stats={data.edgeLabels} />
            <CardinalityColumn title="property keys" stats={data.propertyKeys} />
          </div>

          <DegreeTable shape={data} />

          <div className="panel overflow-x-auto">
            <div className="panel-title">indices</div>
            <table className="w-full text-[12px]">
              <thead>
                <tr className="text-fg-faint">
                  <th className="table-cell">name</th>
                  <th className="table-cell">type</th>
                  <th className="table-cell text-right">keys</th>
                  <th className="table-cell text-right">values</th>
                  <th className="table-cell w-20" />
                </tr>
              </thead>
              <tbody>
                {(data.indices ?? []).map((index) => (
                  <tr key={index.name ?? "—"}>
                    <td className="table-cell font-semibold">{index.name ?? "—"}</td>
                    <td className="table-cell text-fg-dim">{index.type ?? "—"}</td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.keys.toLocaleString()}
                    </td>
                    <td className="table-cell text-fg-dim text-right">
                      {index.values.toLocaleString()}
                    </td>
                    <td className="table-cell">
                      {index.name && (
                        <button
                          type="button"
                          className="btn"
                          onClick={() => {
                            setScanPrefill({ kind: "index", indexId: index.name! });
                            navigate({ to: "/query" });
                          }}
                        >
                          Scan
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {(data.indices ?? []).length === 0 && (
                  <tr>
                    <td className="table-cell text-fg-faint" colSpan={5}>
                      no indices
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}
