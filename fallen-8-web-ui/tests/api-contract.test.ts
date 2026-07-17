import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import openapi from "../../features/done/web-ui/openapi-v0.1.json";
import * as endpoints from "../src/api/endpoints";
import type { InstanceConfig } from "../src/instances/types";

/**
 * Route/serialization correctness against the OpenAPI snapshot (spec §10 "UI unit"):
 * every request the client emits must match a path template + method in
 * features/done/web-ui/openapi-v0.1.json (routes are root-level - never /api/v0.1/), and
 * mutations must carry waitForCompletion=true (FR-21).
 *
 * /delegates/validate is added by this feature and not part of the captured snapshot,
 * so it is asserted separately against the G-2 contract.
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
        body: init?.body ? JSON.parse(init.body as string) : undefined,
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

describe("API client route correctness vs openapi-v0.1.json", () => {
  it("hits only contract routes at root level", async () => {
    await endpoints.getStatus(instance);
    await endpoints.getGraph(instance, 100);
    await endpoints.getVertex(instance, 1);
    await endpoints.getEdge(instance, 2);
    await endpoints.getGraphElement(instance, 3);
    await endpoints.getOutEdgeProperties(instance, 1);
    await endpoints.getInEdgeProperties(instance, 1);
    await endpoints.getOutEdges(instance, 1, "knows");
    await endpoints.getInEdges(instance, 1, "knows");
    await endpoints.getInDegree(instance, 1);
    await endpoints.getOutDegree(instance, 1);
    await endpoints.getEdgePropertyDegree(instance, 1, "out", "knows");
    await endpoints.getEdgeSource(instance, 2);
    await endpoints.getEdgeTarget(instance, 2);
    await endpoints.scanProperty(instance, "age", {
      operator: 0,
      literal: { value: "30", fullQualifiedTypeName: "System.Int32" },
      resultType: "Both",
    });
    await endpoints.scanIndex(instance, {
      indexId: "i",
      operator: 0,
      literal: { value: "x", fullQualifiedTypeName: "System.String" },
      resultType: "Both",
    });
    await endpoints.scanIndexRange(instance, {
      indexId: "i",
      leftLimit: { value: "0", fullQualifiedTypeName: "System.Int32" },
      rightLimit: { value: "9", fullQualifiedTypeName: "System.Int32" },
      includeLeft: true,
      includeRight: true,
      resultType: "Both",
    });
    await endpoints.scanFulltext(instance, { indexId: "i", requestString: "q" });
    await endpoints.scanSpatial(instance, { indexId: "i", graphElementId: 1, distance: 2 });
    await endpoints.createIndex(instance, { uniqueId: "i", pluginType: "DictionaryIndex" });
    await endpoints.addToIndex(instance, "i", {
      graphElementId: 1,
      key: { value: "k", fullQualifiedTypeName: "System.String" },
    });
    await endpoints.removeIndexKey(instance, "i", {
      propertyId: "k",
      propertyValue: "v",
      fullQualifiedTypeName: "System.String",
    });
    await endpoints.removeFromIndex(instance, "i", 1);
    await endpoints.deleteIndex(instance, "i");
    await endpoints.findPaths(instance, 1, 2, {
      pathAlgorithmName: "BLS",
      maxDepth: 7,
      maxResults: 1,
      maxPathWeight: 1,
    });
    await endpoints.listSubGraphNames(instance);
    await endpoints.getSubGraph(instance, "s");
    await endpoints.getSubGraphContents(instance, "s");
    await endpoints.createSubGraph(instance, { name: "s" });
    await endpoints.recalculateSubGraph(instance, "s");
    await endpoints.deleteSubGraph(instance, "s");
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
    await endpoints.saveGraph(instance);
    await endpoints.listSaveGames(instance);
    await endpoints.getSaveGame(instance, "sg-1");
    await endpoints.loadSaveGame(instance, "sg-1");
    await endpoints.deleteSaveGame(instance, "sg-1", true);
    await endpoints.loadGraph(instance, "p");
    await endpoints.trimGraph(instance);
    await endpoints.tabulaRasa(instance);
    await endpoints.generateSampleGraph(instance);
    await endpoints.runBenchmark(instance);
    await endpoints.getStatistics(instance);
    await endpoints.listStoredQueries(instance);
    await endpoints.getStoredQuery(instance, "q");
    await endpoints.registerStoredQuery(instance, {
      name: "q",
      kind: "Path",
      path: { filter: { vertexFilter: "return (v) => true;" } },
    });
    await endpoints.deleteStoredQuery(instance, "q");
    await endpoints.findPaths(instance, 1, 2, {
      pathAlgorithmName: "BLS",
      maxDepth: 7,
      maxResults: 1,
      maxPathWeight: 1,
      storedQuery: "q",
    });
    await endpoints.createSubGraph(instance, { name: "s", storedQuery: "q" });
    await endpoints.scanVector(instance, {
      indexId: "emb",
      query: [0.1, 0.2],
      k: 10,
      kind: "vertex",
      label: "person",
    });
    await endpoints.addVectorToIndex(instance, "emb", {
      graphElementId: 1,
      propertyId: "embedding",
    });
    await endpoints.putElementEmbedding(instance, 1, "default", { vector: [0.1, 0.2] });
    await endpoints.deleteElementEmbedding(instance, 1, "default");
    await endpoints.embedElement(instance, { graphElementId: 1, text: "a red bicycle" });
    await endpoints.embeddingSearch(instance, { indexId: "emb", text: "red bicycles", k: 10 });
    await endpoints.listAnalyticsAlgorithms(instance);
    await endpoints.runAnalytics(instance, "PAGERANK", {
      vertexLabel: "person",
      maxResults: 100,
      writeBack: true,
      writeBackPropertyKey: "analytics.pagerank",
    });
    await endpoints.getPartitionMembers(instance, "WCC", 0, { maxResults: 100, offset: 0 });
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        const parsed = new URL(url);
        recorded.push({
          method: init?.method ?? "GET",
          path: parsed.pathname,
          query: parsed.searchParams,
          body: undefined,
        });
        return new Response("", { status: 200 });
      }),
    );
    await endpoints.exportBulk(instance, { vertexLabel: "person" });
    await endpoints.importBulk(instance, new Blob(['{"type":"meta"}\n']));

    expect(recorded.length).toBeGreaterThan(30);
    for (const call of recorded) {
      expect(call.path, "routes must be root-level").not.toMatch(/^\/api\//);
      assertInContract(call);
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
    await endpoints.exportBulk(instance, { vertexLabel: "person", edgeLabel: "knows" });
    await endpoints.exportBulk(instance);
    await endpoints.importBulk(instance, new Blob(['{"type":"meta"}\n']));

    const exportUrl = new URL(calls[0].url);
    expect(exportUrl.pathname).toBe("/bulk/export");
    expect(exportUrl.searchParams.get("vertexLabel")).toBe("person");
    expect(exportUrl.searchParams.get("edgeLabel")).toBe("knows");
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
