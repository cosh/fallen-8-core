// MIT License
//
// api-contract.test.ts
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

import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import openapi from "../../features/done/web-ui/openapi-v0.1.json";
import * as endpoints from "../src/api/endpoints";
import type { InstanceConfig } from "../src/instances/types";

/**
 * Route/serialization correctness against the OpenAPI snapshot (spec §10 "UI unit"):
 * every request the client emits must match a path template + method in
 * features/done/web-ui/openapi-v0.1.json (routes are root-level - never /api/v0.1/), and
 * mutations must carry waitForCompletion=true (FR-21). The completeness sweep (CA-22)
 * reflects over the endpoints module, so every route-bearing export is exercised against
 * the snapshot; /delegates/validate additionally pins its G-2 body shape below.
 */

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

interface Recorded {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
  /** The bytes the server would read. Parsing hides a double-encoded body; this does not. */
  rawBody: string | undefined;
}

let recorded: Recorded[] = [];

beforeEach(() => {
  recorded = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const parsed = new URL(url);
      recorded.push({
        method: init?.method ?? "GET",
        path: parsed.pathname,
        query: parsed.searchParams,
        // Only JSON bodies are parsed; FormData (ingestFile) and Blob (importBulk) pass through
        // as undefined instead of throwing in JSON.parse.
        body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
        rawBody: typeof init?.body === "string" ? init.body : undefined,
      });
      return new Response("null", { status: 200 });
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const templates = Object.keys((openapi as { paths: Record<string, unknown> }).paths);

function matchesTemplate(path: string, template: string): boolean {
  const pathParts = path.split("/").filter(Boolean);
  const templateParts = template.split("/").filter(Boolean);
  if (pathParts.length !== templateParts.length) return false;
  return templateParts.every(
    (part, i) => part.startsWith("{") || part === pathParts[i],
  );
}

function assertInContract(call: Recorded) {
  const template = templates.find(
    (t) =>
      matchesTemplate(call.path, t) &&
      Object.keys(
        (openapi as { paths: Record<string, Record<string, unknown>> }).paths[t],
      ).includes(call.method.toLowerCase()),
  );
  expect(
    template,
    `${call.method} ${call.path} is not in the OpenAPI contract`,
  ).toBeDefined();
}

// Every route-bearing endpoint export must be exercised against the contract below, so a new
// endpoint cannot ship without a route/method check (CA-22). Exports that issue no request, or
// whose routes are pinned through another export, are excluded WITH a reason.
const EXCLUDED_FROM_CONTRACT_SWEEP = new Map<string, string>([
  ["isAuthorized", "pure predicate over StatusREST; issues no request"],
  ["listSubGraphSummaries", "composite helper; its routes are pinned via listSubGraphNames + getSubGraph"],
]);

// One representative call per route-bearing endpoint. Arguments only need to be well-formed
// enough to reach fetch; the assertion is route + method against the OpenAPI snapshot.
const ENDPOINT_CALLS: Record<string, () => Promise<unknown>> = {
  getStatus: () => endpoints.getStatus(instance),
  getConfig: () => endpoints.getConfig(instance),
  writeConfig: () => endpoints.writeConfig(instance, { settings: { "Fallen8:Plugins:MaxCount": "128" } }),
  postChat: () => endpoints.postChat(instance, { messages: [{ role: "user", content: "hi" }] }),
  getStatistics: () => endpoints.getStatistics(instance),
  saveGraph: () => endpoints.saveGraph(instance),
  saveAllNamespaces: () => endpoints.saveAllNamespaces(instance),
  tabulaRasaAll: () => endpoints.tabulaRasaAll(instance),
  listNamespaces: () => endpoints.listNamespaces(instance),
  createNamespace: () => endpoints.createNamespace(instance, "analytics"),
  renameNamespace: () => endpoints.renameNamespace(instance, "analytics", "analytics-2"),
  setNamespaceLoadOnStartup: () =>
    endpoints.setNamespaceLoadOnStartup(instance, "analytics", "disabled"),
  activateNamespace: () => endpoints.activateNamespace(instance, "analytics"),
  dropNamespace: () => endpoints.dropNamespace(instance, "analytics"),
  listSaveGames: () => endpoints.listSaveGames(instance),
  getSaveGame: () => endpoints.getSaveGame(instance, "sg-1"),
  loadSaveGame: () => endpoints.loadSaveGame(instance, "sg-1"),
  deleteSaveGame: () => endpoints.deleteSaveGame(instance, "sg-1", true),
  loadGraph: () => endpoints.loadGraph(instance, "p"),
  trimGraph: () => endpoints.trimGraph(instance),
  tabulaRasa: () => endpoints.tabulaRasa(instance),
  generateGraph: () => endpoints.generateGraph(instance),
  runBenchmark: () => endpoints.runBenchmark(instance),
  exportBulk: () => endpoints.exportBulk(instance, { vertexLabel: "person" }),
  importBulk: () => endpoints.importBulk(instance, new Blob(['{"type":"meta"}\n'])),
  getGraph: () => endpoints.getGraph(instance, 100),
  getVertex: () => endpoints.getVertex(instance, 1),
  getEdge: () => endpoints.getEdge(instance, 2),
  getGraphElement: () => endpoints.getGraphElement(instance, 3),
  getGraphElements: () => endpoints.getGraphElements(instance, [3, 4]),
  getOutEdgeProperties: () => endpoints.getOutEdgeProperties(instance, 1),
  getInEdgeProperties: () => endpoints.getInEdgeProperties(instance, 1),
  getOutEdges: () => endpoints.getOutEdges(instance, 1, "knows"),
  getInEdges: () => endpoints.getInEdges(instance, 1, "knows"),
  getInDegree: () => endpoints.getInDegree(instance, 1),
  getOutDegree: () => endpoints.getOutDegree(instance, 1),
  getEdgePropertyDegree: () => endpoints.getEdgePropertyDegree(instance, 1, "out", "knows"),
  getEdgeSource: () => endpoints.getEdgeSource(instance, 2),
  getEdgeTarget: () => endpoints.getEdgeTarget(instance, 2),
  scanProperty: () =>
    endpoints.scanProperty(instance, "age", {
      operator: 0,
      literal: { value: "30", fullQualifiedTypeName: "System.Int32" },
      resultType: "Both",
    }),
  scanProperties: () =>
    endpoints.scanProperties(instance, { searchTerm: "acme", resultType: "Both" }),
  scanIndex: () =>
    endpoints.scanIndex(instance, {
      indexId: "i",
      operator: 0,
      literal: { value: "x", fullQualifiedTypeName: "System.String" },
      resultType: "Both",
    }),
  scanIndexRange: () =>
    endpoints.scanIndexRange(instance, {
      indexId: "i",
      leftLimit: { value: "0", fullQualifiedTypeName: "System.Int32" },
      rightLimit: { value: "9", fullQualifiedTypeName: "System.Int32" },
      includeLeft: true,
      includeRight: true,
      resultType: "Both",
    }),
  scanFulltext: () => endpoints.scanFulltext(instance, { indexId: "i", requestString: "q" }),
  scanSpatial: () => endpoints.scanSpatial(instance, { indexId: "i", graphElementId: 1, distance: 2 }),
  scanVector: () =>
    endpoints.scanVector(instance, {
      indexId: "emb",
      query: [0.1, 0.2],
      k: 10,
      kind: "vertex",
      label: "person",
    }),
  addVectorToIndex: () =>
    endpoints.addVectorToIndex(instance, "emb", { graphElementId: 1, propertyId: "embedding" }),
  putElementEmbedding: () => endpoints.putElementEmbedding(instance, 1, "default", { vector: [0.1, 0.2] }),
  deleteElementEmbedding: () => endpoints.deleteElementEmbedding(instance, 1, "default"),
  embedElement: () => endpoints.embedElement(instance, { graphElementId: 1, text: "a red bicycle" }),
  embeddingSearch: () => endpoints.embeddingSearch(instance, { indexId: "emb", text: "red bicycles", k: 10 }),
  createIndex: () => endpoints.createIndex(instance, { uniqueId: "i", pluginType: "DictionaryIndex" }),
  addToIndex: () =>
    endpoints.addToIndex(instance, "i", {
      graphElementId: 1,
      key: { propertyValue: "k", fullQualifiedTypeName: "System.String" },
    }),
  removeIndexKey: () =>
    endpoints.removeIndexKey(instance, "i", { propertyValue: "v", fullQualifiedTypeName: "System.String" }),
  removeFromIndex: () => endpoints.removeFromIndex(instance, "i", 1),
  deleteIndex: () => endpoints.deleteIndex(instance, "i"),
  findPaths: () =>
    endpoints.findPaths(instance, 1, 2, {
      pathAlgorithmName: "BLS",
      maxDepth: 7,
      maxResults: 1,
      maxPathWeight: 1,
    }),
  listSubGraphNames: () => endpoints.listSubGraphNames(instance),
  getSubGraph: () => endpoints.getSubGraph(instance, "s"),
  getSubGraphContents: () => endpoints.getSubGraphContents(instance, "s"),
  createSubGraph: () => endpoints.createSubGraph(instance, { name: "s" }),
  recalculateSubGraph: () => endpoints.recalculateSubGraph(instance, "s"),
  deleteSubGraph: () => endpoints.deleteSubGraph(instance, "s"),
  listAnalyticsAlgorithms: () => endpoints.listAnalyticsAlgorithms(instance),
  runAnalytics: () =>
    endpoints.runAnalytics(instance, "PAGERANK", {
      vertexLabel: "person",
      maxResults: 100,
      writeBack: true,
      writeBackPropertyKey: "analytics.pagerank",
    }),
  getPartitionMembers: () =>
    endpoints.getPartitionMembers(instance, "WCC", 0, { maxResults: 100, offset: 0 }),
  listStoredQueries: () => endpoints.listStoredQueries(instance),
  getStoredQuery: () => endpoints.getStoredQuery(instance, "q"),
  registerStoredQuery: () =>
    endpoints.registerStoredQuery(instance, {
      name: "q",
      kind: "Path",
      path: { filter: { vertexFilter: "return (v) => true;" } },
    }),
  deleteStoredQuery: () => endpoints.deleteStoredQuery(instance, "q"),
  listPlugins: () => endpoints.listPlugins(instance),
  getPlugin: () => endpoints.getPlugin(instance, "MyFunc"),
  registerAlgorithmPlugin: () =>
    endpoints.registerAlgorithmPlugin(instance, { name: "MyDijkstra", contract: "Path", sourceCode: "class X {}" }),
  registerFunctionPlugin: () =>
    endpoints.registerFunctionPlugin(instance, { name: "MyFunc", sourceCode: "class X {}" }),
  deletePlugin: () => endpoints.deletePlugin(instance, "MyFunc"),
  invokeGraphFunction: () => endpoints.invokeGraphFunction(instance, "MyFunc", { label: "person" }),
  validatePlugin: () =>
    endpoints.validatePlugin(instance, "algorithm", { name: "MyDijkstra", contract: "Path", sourceCode: "class X {}" }),
  createVertex: () => endpoints.createVertex(instance, { creationDate: 0 }),
  createEdge: () =>
    endpoints.createEdge(instance, { creationDate: 0, sourceVertex: 1, targetVertex: 2, edgePropertyId: "knows" }),
  setProperty: () =>
    endpoints.setProperty(instance, 1, "age", {
      propertyId: "age",
      propertyValue: "30",
      fullQualifiedTypeName: "System.Int32",
    }),
  removeProperty: () => endpoints.removeProperty(instance, 1, "age"),
  removeGraphElement: () => endpoints.removeGraphElement(instance, 1),
  validateDelegate: () => endpoints.validateDelegate(instance, "VertexFilter", "return (v) => true;"),
  listDocuments: () => endpoints.listDocuments(instance),
  getDocument: () => endpoints.getDocument(instance, 1),
  deleteDocument: () => endpoints.deleteDocument(instance, 1),
  ingestText: () => endpoints.ingestText(instance, { name: "doc", text: "hello world" }),
  ingestFile: () => endpoints.ingestFile(instance, new File(["body"], "a.txt", { type: "text/plain" })),
  searchDocuments: () => endpoints.searchDocuments(instance, { queryText: "graphs" }),
  getDocumentBinding: () => endpoints.getDocumentBinding(instance),
  ensureDocumentBinding: () => endpoints.ensureDocumentBinding(instance),
  listEntities: () => endpoints.listEntities(instance),
  listIntegrationProviders: () => endpoints.listIntegrationProviders(instance),
  getIntegrationRun: () => endpoints.getIntegrationRun(instance, "office-inventory"),
  submitIntegrationJob: () =>
    endpoints.submitIntegrationJob(instance, {
      providerId: "csv-device-list",
      integrationInstanceId: "office-inventory",
      // The file's BYTES ride in `files`, never its name in `settings`: the runtime opens nothing on
      // disk and refuses a file setting named there. This test only checks route and method, so the
      // shape here is a statement of the real wire body rather than something it can verify.
      settings: {},
      credentialValues: {},
      files: { file: { name: "devices.csv", contentBase64: "bWFjCg==" } },
    }),
};

describe("API client route correctness vs openapi-v0.1.json", () => {
  it("exercises every route-bearing endpoint against the contract (CA-22 completeness)", async () => {
    const exported = Object.keys(endpoints).filter(
      (name) => typeof (endpoints as Record<string, unknown>)[name] === "function",
    );
    const routeBearing = exported.filter((name) => !EXCLUDED_FROM_CONTRACT_SWEEP.has(name));

    // A new endpoint export must be registered here (or excluded with a reason); otherwise it
    // would ship with no route/method check - the gap this test closes.
    const unregistered = routeBearing.filter((name) => !(name in ENDPOINT_CALLS)).sort();
    expect(
      unregistered,
      "register a contract call for each new endpoint (or exclude it with a reason)",
    ).toEqual([]);

    // Neither the exclusions nor the registry may name a function that no longer exists.
    const staleExclusions = [...EXCLUDED_FROM_CONTRACT_SWEEP.keys()]
      .filter((name) => !exported.includes(name))
      .sort();
    expect(staleExclusions, "drop exclusions for endpoints that no longer exist").toEqual([]);
    const staleRegistered = Object.keys(ENDPOINT_CALLS)
      .filter((name) => !exported.includes(name))
      .sort();
    expect(staleRegistered, "drop registry entries for endpoints that no longer exist").toEqual([]);

    // Each registered endpoint issues at least one request, and every request is a root-level
    // route + method that exists in the OpenAPI snapshot.
    for (const [name, call] of Object.entries(ENDPOINT_CALLS)) {
      recorded = [];
      await call();
      expect(recorded.length, `${name} issued no request`).toBeGreaterThan(0);
      for (const request of recorded) {
        expect(request.path, "routes must be root-level").not.toMatch(/^\/api\//);
        assertInContract(request);
        // No endpoint sends a JSON string as its whole body: apiRequest serializes what it is
        // given, so a caller that pre-serializes ships a double-encoded body the model binder
        // answers with 400. Reaching a route the snapshot knows is not enough if the payload
        // arrives as a quoted string.
        expect(
          typeof request.body,
          `${name} sends a double-encoded body: ${String(request.rawBody)}`,
        ).not.toBe("string");
      }
    }
  });

  it("sends waitForCompletion=true on every mutation (FR-21)", async () => {
    await endpoints.createVertex(instance, { creationDate: 0 });
    await endpoints.createEdge(instance, {
      creationDate: 0,
      sourceVertex: 1,
      targetVertex: 2,
      edgePropertyId: "knows",
    });
    await endpoints.setProperty(instance, 1, "age", {
      propertyId: "age",
      propertyValue: "30",
      fullQualifiedTypeName: "System.Int32",
    });
    await endpoints.removeProperty(instance, 1, "age");
    await endpoints.removeGraphElement(instance, 1);
    await endpoints.tabulaRasa(instance);

    for (const call of recorded) {
      expect(call.query.get("waitForCompletion"), `${call.method} ${call.path}`).toBe(
        "true",
      );
    }
  });

  it("serializes typed literals in camelCase (FR-9)", async () => {
    await endpoints.scanProperty(instance, "age", {
      operator: 0,
      literal: { value: "30", fullQualifiedTypeName: "System.Int32" },
      resultType: "Vertices",
    });
    const body = recorded[0].body as Record<string, unknown>;
    expect(body).toHaveProperty("operator", 0);
    expect(body).toHaveProperty("literal");
    expect((body.literal as Record<string, unknown>).fullQualifiedTypeName).toBe(
      "System.Int32",
    );
    expect(JSON.stringify(body)).not.toMatch(/FullQualifiedTypeName/);
  });

  it("the batch element read puts a JSON ARRAY on the wire, not a quoted array", async () => {
    await endpoints.getGraphElements(instance, [3, 4]);

    const call = recorded[0];
    expect(call.method).toBe("POST");
    expect(call.path).toBe("/graphelements/get");
    // The server binds [FromBody] List<Int32>: "[3,4]" as a JSON string is a 400, and hydration
    // swallows that into its per-element fallback, so the batch read would silently never run.
    expect(call.rawBody).toBe("[3,4]");
    expect(Array.isArray(call.body)).toBe(true);
    expect(call.body).toEqual([3, 4]);
  });

  it("embedding element writes send waitForCompletion (FR-21); bodies are camelCase", async () => {
    await endpoints.putElementEmbedding(instance, 7, "default", { vector: [0.1, 0.2] });
    await endpoints.deleteElementEmbedding(instance, 7, "default");
    await endpoints.embedElement(instance, { graphElementId: 7, text: "x", name: "title" });
    await endpoints.embeddingSearch(instance, {
      indexId: "emb",
      text: "x",
      k: 5,
      kind: "vertex",
    });

    const put = recorded[0];
    expect(put.method).toBe("PUT");
    expect(put.path).toBe("/graphelement/7/embedding/default");
    // The element embedding write is a mutation: it must commit before the UI re-reads it,
    // and a rolled-back write must surface (not a fire-and-forget 202).
    expect(put.query.get("waitForCompletion")).toBe("true");
    expect(put.body).toEqual({ vector: [0.1, 0.2] });

    const del = recorded[1];
    expect(del.method).toBe("DELETE");
    expect(del.query.get("waitForCompletion")).toBe("true");

    // Provider embed/search are POSTs whose server awaits completion itself — no query flag.
    expect(recorded[2].body).toEqual({ graphElementId: 7, text: "x", name: "title" });
    expect(recorded[3].body).toEqual({ indexId: "emb", text: "x", k: 5, kind: "vertex" });
  });

  it("calls the G-2 validate endpoint with the agreed contract", async () => {
    await endpoints.validateDelegate(instance, "VertexFilter", "return (v) => true;");
    expect(recorded[0].method).toBe("POST");
    expect(recorded[0].path).toBe("/delegates/validate");
    expect(recorded[0].body).toEqual({
      delegateKind: "VertexFilter",
      fragment: "return (v) => true;",
    });
  });

  it("the startup-load policy PATCH carries only loadOnStartup, never a name", async () => {
    await endpoints.setNamespaceLoadOnStartup(instance, "analytics", "inherit");

    expect(recorded[0].method).toBe("PATCH");
    expect(recorded[0].path).toBe("/ns/analytics");
    // The server applies the whole PATCH body atomically, so a "name" riding along on a policy
    // edit would rename the namespace as a side effect - and a stale one would rename it back.
    expect(recorded[0].body).toEqual({ loadOnStartup: "inherit" });
  });

  it("activation is a bodiless POST on its own sub-route, and no policy edit rides along", async () => {
    await endpoints.activateNamespace(instance, "analytics");

    expect(recorded[0].method).toBe("POST");
    // Its own sub-route, not PATCH /ns/{name}: activation answers for THIS process while the
    // PATCH answers for the next boot, and a caller that could only do both would make every
    // "load it now" permanently change the boot selection.
    expect(recorded[0].path).toBe("/ns/analytics/activate");
    expect(recorded[0].rawBody).toBeUndefined();
    expect(recorded[0].query.has("loadOnStartup")).toBe(false);
  });

  it("subgraph nesting: fromSubGraph travels as a query param, never in the body", async () => {
    await endpoints.createSubGraph(instance, { name: "child" }, "parent");
    await endpoints.createSubGraph(instance, { name: "top" });

    const nested = recorded[0];
    expect(nested.query.get("fromSubGraph")).toBe("parent");
    expect(nested.body).not.toHaveProperty("fromSubGraph");

    // No nesting → the param is absent entirely (absent ≠ empty string).
    expect(recorded[1].query.has("fromSubGraph")).toBe(false);
  });

  it("bulk interchange: export carries label filters, import sends x-ndjson", async () => {
    const calls: { url: string; init?: RequestInit }[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({ url, init });
        return new Response("", { status: 200 });
      }),
    );
    await endpoints.exportBulk(instance, {
      vertexLabel: "person",
      edgeLabel: "friendship",
      edgePropertyId: "knows",
    });
    await endpoints.exportBulk(instance);
    await endpoints.importBulk(instance, new Blob(['{"type":"meta"}\n']));

    const exportUrl = new URL(calls[0].url);
    expect(exportUrl.pathname).toBe("/bulk/export");
    expect(exportUrl.searchParams.get("vertexLabel")).toBe("person");
    expect(exportUrl.searchParams.get("edgeLabel")).toBe("friendship");
    expect(exportUrl.searchParams.get("edgePropertyId")).toBe("knows");
    // Unfiltered export sends NO filter params (server treats absent ≠ empty string).
    expect(new URL(calls[1].url).search).toBe("");
    expect(calls[2].init?.method).toBe("POST");
    expect(new URL(calls[2].url).pathname).toBe("/bulk/import");
    expect(
      (calls[2].init?.headers as Record<string, string>)["Content-Type"],
    ).toBe("application/x-ndjson");
  });

  it("treats 204/empty bodies as null, not an error", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(null, { status: 204 })),
    );
    await expect(endpoints.getVertex(instance, 999)).resolves.toBeNull();
  });
});
