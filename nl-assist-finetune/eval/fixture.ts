/**
 * Semantic-evaluation fixture + comparator (nl-assist-finetune plan phase 4, spec FT-8).
 *
 * Compile-validation (baseline.ts) cannot see a fragment that compiles yet selects the
 * WRONG elements - the field example `TryGetProperty(out string label, "label")` probes
 * the user-property store instead of the built-in Label and still compiles. FT-8 catches
 * that class with NO new server code: seed a fixture graph, then run BOTH the generated
 * and the reference fragment through the existing /subgraph endpoint and compare the
 * element sets they select.
 *
 * Mechanism (reuses the product's own surface):
 *   - /subgraph's vertexFilter/edgeFilter fields take a GraphElementFilter (AGraphElementModel
 *     `ge`). We rewrite the fragment's parameter to `ge` and submit it; a VertexFilter or
 *     GraphElementFilter is compared over the selected VERTICES, an EdgeFilter over the
 *     selected EDGES (GET /subgraph/{name}/graph).
 *   - Kinds with no element-set mapping (EdgePropertyFilter, VertexCost, EdgeCost) and
 *     fragments using a VertexModel/EdgeModel-only member (GetOutDegree, TargetVertex, ...)
 *     that will not compile as a GraphElementFilter are reported "not applicable" and fall
 *     back to the regex proxy - never silently scored.
 *
 * Run standalone to self-test the machinery WITHOUT a model (ref-vs-ref must pass, and a
 * select-nothing negative must be caught):  npx tsx nl-assist-finetune/eval/fixture.ts
 * Meant to run against a DEDICATED volatile apiApp (Fallen8__Durability__Volatile=true)
 * so the fixture is the only data; seeding is idempotent within one instance.
 */

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { DelegateKind } from "../../fallen-8-web-ui/src/api/types";
import { rewriteParameterName } from "../../fallen-8-web-ui/src/delegate/snippets";
import { f8Fetch } from "../shared/f8";

const here = path.dirname(fileURLToPath(import.meta.url));
const runToken = Date.now().toString(36); // unique subgraph-name prefix per process
let subgraphCounter = 0;

// --- fixture definition -----------------------------------------------------------------
// Designed so every evaluable eval-set filter selects a distinctive, mostly-proper subset:
// persons split by age (>30 vs not) and property count, cities/companies/car for label
// tests, names starting with A vs not, edges split by label and by weight.
const S = "System.String";
const I = "System.Int32";
const D = "System.Double";
interface Prop { id: string; type: string; value: string }
interface FVertex { name: string; label: string; props: Prop[] }
interface FEdge { from: string; to: string; edgePropertyId: string; label: string; weight: number }
const p = (id: string, type: string, value: string): Prop => ({ id, type, value });

const VERTICES: FVertex[] = [
  { name: "Alice", label: "person", props: [p("name", S, "Alice"), p("age", I, "35"), p("city", S, "NYC"), p("email", S, "a@x")] },
  { name: "Andrew", label: "person", props: [p("name", S, "Andrew"), p("age", I, "28")] },
  { name: "Bob", label: "person", props: [p("name", S, "Bob"), p("age", I, "42"), p("city", S, "LA")] },
  { name: "Bella", label: "person", props: [p("name", S, "Bella"), p("age", I, "25")] },
  { name: "Carol", label: "person", props: [p("name", S, "Carol"), p("age", I, "60"), p("city", S, "SF"), p("email", S, "c@x"), p("phone", S, "1")] },
  { name: "CityNYC", label: "city", props: [p("name", S, "CityNYC"), p("population", I, "8000000")] },
  { name: "CityLA", label: "city", props: [p("name", S, "CityLA")] },
  { name: "Acme", label: "company", props: [p("name", S, "Acme"), p("revenue", I, "1000000")] },
  { name: "Globex", label: "company", props: [p("name", S, "Globex")] },
  { name: "Tesla", label: "car", props: [p("name", S, "Tesla"), p("price", I, "50000")] },
];
// Weights are integer-valued (still stored as double) on purpose: the apiApp coerces a
// PropertySpecification value with Convert.ChangeType under the host's CurrentCulture
// (ServiceHelper.CreateObject), so a decimal like "0.8" is misparsed to 8 on a
// comma-decimal locale. Integers straddling 0.5 (0 vs >=1) parse identically everywhere,
// keeping the fixture deterministic across machines while ef-weight (> 0.5) still splits.
const EDGES: FEdge[] = [
  { from: "Alice", to: "Bob", edgePropertyId: "knows", label: "knows", weight: 2 },
  { from: "Bob", to: "Carol", edgePropertyId: "knows", label: "knows", weight: 0 },
  { from: "Alice", to: "Andrew", edgePropertyId: "trusts", label: "trusts", weight: 3 },
  { from: "Carol", to: "Bella", edgePropertyId: "likes", label: "likes", weight: 1 },
  { from: "Bob", to: "Alice", edgePropertyId: "knows", label: "knows", weight: 0 },
  { from: "Andrew", to: "Bob", edgePropertyId: "worksWith", label: "worksWith", weight: 2 },
];

interface ApiVertex { id: number; label: string; properties?: { propertyId: string; propertyValue: string }[] }
interface ApiGraph { vertices?: ApiVertex[]; edges?: { id: number }[] }

async function getGraph(pathSuffix = "/graph"): Promise<ApiGraph> {
  const response = await f8Fetch(pathSuffix);
  if (!response.ok) throw new Error(`GET ${pathSuffix} -> HTTP ${response.status}`);
  return (await response.json()) as ApiGraph;
}

/** Seed the fixture once per instance (idempotent: skips if the sentinel vertex exists). */
export async function ensureFixture(): Promise<{ seeded: boolean; vertices: number; edges: number }> {
  const existing = await getGraph("/graph");
  const nameOf = (v: ApiVertex) => v.properties?.find((x) => x.propertyId === "name")?.propertyValue;
  const present = (existing.vertices ?? []).some((v) => v.label === "person" && nameOf(v) === "Alice");
  if (present) return { seeded: false, vertices: VERTICES.length, edges: EDGES.length };

  for (const v of VERTICES) {
    const res = await f8Fetch("/vertex?waitForCompletion=true", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        creationDate: 0,
        label: v.label,
        properties: v.props.map((x) => ({ propertyId: x.id, fullQualifiedTypeName: x.type, propertyValue: x.value })),
      }),
    });
    if (!res.ok) throw new Error(`seed vertex ${v.name} -> HTTP ${res.status} ${await res.text().catch(() => "")}`);
  }

  // Map each seeded vertex's unique name -> engine id, then wire edges by id.
  const seededGraph = await getGraph("/graph");
  const idByName = new Map<string, number>();
  for (const v of seededGraph.vertices ?? []) {
    const n = nameOf(v);
    if (n) idByName.set(n, v.id);
  }
  for (const e of EDGES) {
    const source = idByName.get(e.from);
    const target = idByName.get(e.to);
    if (source === undefined || target === undefined) throw new Error(`edge endpoint missing: ${e.from}->${e.to}`);
    const res = await f8Fetch("/edge?waitForCompletion=true", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        creationDate: 0,
        sourceVertex: source,
        targetVertex: target,
        edgePropertyId: e.edgePropertyId,
        label: e.label,
        properties: [{ propertyId: "weight", fullQualifiedTypeName: D, propertyValue: String(e.weight) }],
      }),
    });
    if (!res.ok) throw new Error(`seed edge ${e.from}->${e.to} -> HTTP ${res.status} ${await res.text().catch(() => "")}`);
  }
  return { seeded: true, vertices: VERTICES.length, edges: EDGES.length };
}

/** Where a kind's fragment applies in a subgraph, or null when it has no element-set mapping. */
function placement(kind: DelegateKind): "vertex" | "edge" | null {
  if (kind === "VertexFilter" || kind === "GraphElementFilter") return "vertex";
  if (kind === "EdgeFilter") return "edge";
  return null; // EdgePropertyFilter / VertexCost / EdgeCost
}

interface FilterResult { evaluable: boolean; ids?: Set<number>; reason?: string }

/** Run a fragment as a subgraph filter and return the selected element ids (or why not). */
async function applyFilter(kind: DelegateKind, fragment: string): Promise<FilterResult> {
  const place = placement(kind);
  if (!place) return { evaluable: false, reason: `${kind} has no subgraph element-set mapping` };

  const geFragment = rewriteParameterName(fragment, "ge");
  const name = `f8sem_${runToken}_${++subgraphCounter}`;
  const spec: Record<string, unknown> = { name };
  spec[place === "vertex" ? "vertexFilter" : "edgeFilter"] = geFragment;

  const create = await f8Fetch("/subgraph", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(spec),
  });
  if (create.status === 400) {
    // Won't compile as a GraphElementFilter (a VertexModel/EdgeModel-only member such as
    // GetOutDegree/TargetVertex) - not evaluable via subgraph.
    await create.text().catch(() => undefined);
    return { evaluable: false, reason: "not expressible as GraphElementFilter" };
  }
  if (!create.ok) {
    throw new Error(`subgraph create -> HTTP ${create.status} ${await create.text().catch(() => "")}`);
  }
  try {
    const graph = await getGraph(`/subgraph/${encodeURIComponent(name)}/graph`);
    const list = place === "vertex" ? graph.vertices : graph.edges;
    return { evaluable: true, ids: new Set((list ?? []).map((x) => x.id)) };
  } finally {
    await f8Fetch(`/subgraph/${encodeURIComponent(name)}`, { method: "DELETE" }).catch(() => undefined);
  }
}

export interface SemanticVerdict {
  applicable: boolean;
  pass?: boolean;
  refCount?: number;
  genCount?: number;
  reason?: string;
}

const setsEqual = (a: Set<number>, b: Set<number>) => a.size === b.size && [...a].every((x) => b.has(x));

/**
 * Semantic verdict for one row: does `fragment` select the same elements as `reference`?
 * applicable=false when the row can't be element-set-compared (the reference isn't
 * expressible as a GraphElementFilter, or the kind has no mapping) - such rows keep the
 * regex proxy. A generated fragment that compiles elsewhere but not as a GraphElementFilter
 * (while the reference does) is a semantic miss, not "n/a".
 */
export async function compareSemantics(
  kind: DelegateKind,
  reference: string,
  fragment: string,
): Promise<SemanticVerdict> {
  const ref = await applyFilter(kind, reference);
  if (!ref.evaluable) return { applicable: false, reason: `reference ${ref.reason}` };
  const gen = await applyFilter(kind, fragment);
  if (!gen.evaluable) return { applicable: true, pass: false, refCount: ref.ids!.size, reason: `generated ${gen.reason}` };
  return { applicable: true, pass: setsEqual(ref.ids!, gen.ids!), refCount: ref.ids!.size, genCount: gen.ids!.size };
}

// --- standalone self-test (no model) ----------------------------------------------------
interface EvalRow { id: string; kind: DelegateKind; reference: string }

async function main() {
  const info = await ensureFixture();
  console.log(`fixture: ${info.seeded ? "seeded" : "already present"} (${info.vertices} vertices, ${info.edges} edges)`);

  const rows = (JSON.parse(readFileSync(path.join(here, "eval-set.json"), "utf8")) as { rows: EvalRow[] }).rows;
  let evaluated = 0;
  let refFail = 0;
  let negTotal = 0;
  let negCaught = 0;

  for (const row of rows) {
    const refVsRef = await compareSemantics(row.kind, row.reference, row.reference);
    if (!refVsRef.applicable) {
      console.log(`  n/a   ${row.id} (${refVsRef.reason})`);
      continue;
    }
    evaluated++;
    if (!refVsRef.pass) refFail++;
    console.log(`  ${refVsRef.pass ? "ok  " : "BUG "} ${row.id} ref-vs-ref pass=${refVsRef.pass} refCount=${refVsRef.refCount}`);

    // Negative: a select-nothing fragment must differ from any non-empty reference.
    if ((refVsRef.refCount ?? 0) > 0) {
      negTotal++;
      const neg = await compareSemantics(row.kind, row.reference, "return (ge) => false;");
      if (neg.applicable && neg.pass === false) negCaught++;
      else console.log(`       NEG NOT CAUGHT for ${row.id}: ${JSON.stringify(neg)}`);
    }
  }

  console.log(`\nevaluable rows: ${evaluated}; ref-vs-ref failures (should be 0): ${refFail}; negatives caught: ${negCaught}/${negTotal}`);
  if (refFail > 0 || negCaught !== negTotal) {
    console.error("SELF-TEST FAILED: the semantic comparator is not behaving as expected.");
    process.exit(1);
  }
  console.log("self-test OK");
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith("fixture.ts")) {
  main().catch((error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  });
}
