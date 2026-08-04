// MIT License
//
// sample-loader-documents.test.ts
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

import { afterEach, describe, expect, it, vi } from "vitest";
import { ingestionGate, loadSampleGraph, type LoadStep } from "../src/lib/sampleLoader";
import type { SampleManifestEntry } from "../src/lib/samples";
import type { InstanceConfig } from "../src/instances/types";
import type { StatusREST } from "../src/api/types";

/**
 * The document-bearing sample path (feature knowledge-demo): seed the equality index because
 * `POST /index` does not backfill, bind the semantic layer, then ingest through the real
 * pipeline. Every assertion here guards a failure mode that would produce a load which LOOKS
 * successful but demos nothing: an unseeded index links no chunk to any asset, and a skipped
 * binding means ingestion 428s.
 */

const instance: InstanceConfig = {
  id: "test",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
} as InstanceConfig;

/** A minimal manifest entry; specs override just the field under test. */
function entryWith(overrides: Partial<SampleManifestEntry> = {}): SampleManifestEntry {
  return {
    id: "wind-farm",
    title: "Wind Farm",
    emoji: "🌬️",
    pitch: "",
    vertexCount: 2,
    edgeCount: 1,
    badges: ["knowledge"],
    trySteps: [],
    file: "wind-farm.jsonl",
    styleConfig: {},
    indexRecipes: [{ uniqueId: "asset-tags", pluginType: "DictionaryIndex", pluginOptions: {} }],
    embedding: null,
    ...overrides,
  };
}

/**
 * A routing fetch stub. `calls` records every request so a test can assert what the loader
 * did and, just as importantly, what it did NOT do.
 */
interface Recorded {
  url: string;
  method: string;
  body?: unknown;
}

function stubFetch(options: {
  vertices?: { id: number; properties?: { propertyId: string; propertyValue: unknown }[] }[];
  addToIndexResult?: boolean;
  bindingReady?: boolean;
  documentStates?: string[];
  documentError?: string;
} = {}) {
  const calls: Recorded[] = [];
  const states = [...(options.documentStates ?? ["indexed"])];

  const fetchStub = vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const url = String(input);
    const method = (init?.method ?? "GET").toUpperCase();
    let body: unknown;
    if (typeof init?.body === "string") {
      try {
        body = JSON.parse(init.body);
      } catch {
        body = init.body;
      }
    } else if (init?.body instanceof FormData) {
      body = Object.fromEntries(
        [...init.body.entries()].map(([k, v]) => [k, v instanceof File ? `file:${v.name}` : v]),
      );
    }
    calls.push({ url, method, body });

    const json = (value: unknown, status = 200) =>
      new Response(JSON.stringify(value), {
        status,
        headers: { "Content-Type": "application/json" },
      });

    // Sample assets (fetched from the samples base, not the API).
    if (url.endsWith("wind-farm.jsonl")) return new Response("{}\n");
    if (url.includes("/documents/")) return new Response("# doc\n\nbody");

    if (url.includes("/bulk/import")) return json({ verticesCreated: 2, edgesCreated: 1 });
    if (url.includes("/index/") && method === "PUT") {
      return json(options.addToIndexResult ?? true);
    }
    if (url.includes("/index") && method === "POST") return json(true);
    if (url.includes("/graph")) return json({ vertices: options.vertices ?? [], edges: [] });
    if (url.includes("/document/binding/ensure")) {
      const ready = options.bindingReady ?? true;
      return json({
        ready,
        vector: { role: "vector", indexId: "documents", required: true, exists: ready, ready },
        fulltext: {
          role: "fulltext",
          indexId: "documents-text",
          required: true,
          exists: true,
          ready: true,
        },
        entity: {
          role: "entity",
          indexId: "documents-entities",
          required: true,
          exists: true,
          ready: true,
        },
      });
    }
    if (url.includes("/document/text") || (url.endsWith("/document") && method === "POST")) {
      return json({ documentId: 7, name: "d", status: "processing", chunkCount: 0 }, 202);
    }
    if (/\/document\/\d+$/.test(url) && method === "GET") {
      const status = states.length > 1 ? states.shift() : states[0];
      // Two pseudo-states let a test drive the non-terminal paths: "boom" is a transport-level
      // failure (the poll loop should retry it) and "gone" is a 204/empty body.
      if (status === "boom") return json({ title: "upstream exploded" }, 502);
      if (status === "gone") return new Response(null, { status: 204 });
      return json({
        summary: {
          documentId: 7,
          name: "d",
          status,
          chunkCount: 4,
          error: options.documentError,
        },
        chunks: [],
      });
    }
    if (url.includes("/tabularasa")) return new Response(null, { status: 200 });
    return json(null);
  });

  vi.stubGlobal("fetch", fetchStub);
  return calls;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("loadSampleGraph: index seeding", () => {
  it("adds one index key per vertex carrying the property and skips the rest", async () => {
    const calls = stubFetch({
      vertices: [
        { id: 10, properties: [{ propertyId: "assetTag", propertyValue: "WTG_A17" }] },
        { id: 11, properties: [{ propertyId: "assetTag", propertyValue: "GBX_A17_02" }] },
        // A technician: no assetTag, so it must NOT be seeded (prose names never extract).
        { id: 12, properties: [{ propertyId: "name", propertyValue: "Priya Raman" }] },
      ],
    });

    const result = await loadSampleGraph(
      instance,
      entryWith({ indexSeeds: [{ indexId: "asset-tags", propertyId: "assetTag" }] }),
      "/samples",
      { wipeFirst: false },
    );

    expect(result.tagsSeeded).toBe(2);
    const seedCalls = calls.filter((c) => c.method === "PUT" && c.url.includes("/index/asset-tags"));
    expect(seedCalls).toHaveLength(2);
    expect(seedCalls.map((c) => c.body)).toEqual([
      { graphElementId: 10, key: { propertyValue: "WTG_A17", fullQualifiedTypeName: "System.String" } },
      { graphElementId: 11, key: { propertyValue: "GBX_A17_02", fullQualifiedTypeName: "System.String" } },
    ]);
  });

  it("fails loudly when the server refuses an add, instead of linking nothing later", async () => {
    stubFetch({
      // Two vertices, matching the stubbed import's verticesCreated, so this exercises the
      // refusal path rather than the truncation guard below.
      vertices: [
        { id: 10, properties: [{ propertyId: "assetTag", propertyValue: "WTG_A17" }] },
        { id: 11, properties: [{ propertyId: "name", propertyValue: "Priya Raman" }] },
      ],
      addToIndexResult: false,
    });

    await expect(
      loadSampleGraph(
        instance,
        entryWith({ indexSeeds: [{ indexId: "asset-tags", propertyId: "assetTag" }] }),
        "/samples",
        { wipeFirst: false },
      ),
    ).rejects.toThrow(/Could not add vertex 10 \('WTG_A17'\) to index 'asset-tags'/);
  });

  it("refuses to seed from a truncated graph read rather than link only part of it", async () => {
    // GET /graph returns one page and never says it truncated, so seeding a subset would report
    // success and then silently under-link. The import count is the ground truth.
    stubFetch({
      vertices: [{ id: 10, properties: [{ propertyId: "assetTag", propertyValue: "WTG_A17" }] }],
    });

    await expect(
      loadSampleGraph(
        instance,
        entryWith({ indexSeeds: [{ indexId: "asset-tags", propertyId: "assetTag" }] }),
        "/samples",
        { wipeFirst: false },
      ),
    ).rejects.toThrow(/Only 1 of 2 imported vertices came back in one page/);
  });

  it("counts progress across every seed, not per seed", async () => {
    stubFetch({
      vertices: [
        { id: 10, properties: [{ propertyId: "assetTag", propertyValue: "A_001" }] },
        { id: 11, properties: [{ propertyId: "altTag", propertyValue: "B_001" }] },
      ],
    });
    const seen: [LoadStep, string | undefined][] = [];

    const result = await loadSampleGraph(
      instance,
      entryWith({
        indexSeeds: [
          { indexId: "asset-tags", propertyId: "assetTag" },
          { indexId: "alt-tags", propertyId: "altTag" },
        ],
      }),
      "/samples",
      { wipeFirst: false, onStep: (step, detail) => seen.push([step, detail]) },
    );

    expect(result.tagsSeeded).toBe(2);
    // Denominator is the total across both seeds; a per-seed denominator would say "2/1".
    expect(seen).toContainEqual(["seeding", "1/2"]);
    expect(seen).toContainEqual(["seeding", "2/2"]);
  });

  it("reports seeding progress with a running count", async () => {
    stubFetch({
      vertices: [
        { id: 10, properties: [{ propertyId: "assetTag", propertyValue: "A_001" }] },
        { id: 11, properties: [{ propertyId: "assetTag", propertyValue: "A_002" }] },
      ],
    });
    const seen: [LoadStep, string | undefined][] = [];

    await loadSampleGraph(
      instance,
      entryWith({ indexSeeds: [{ indexId: "asset-tags", propertyId: "assetTag" }] }),
      "/samples",
      { wipeFirst: false, onStep: (step, detail) => seen.push([step, detail]) },
    );

    expect(seen).toContainEqual(["seeding", "1/2"]);
    expect(seen).toContainEqual(["seeding", "2/2"]);
  });
});

describe("loadSampleGraph: binding and ingestion", () => {
  const documents = [
    { file: "documents/a.md", name: "a.md", kind: "text" as const, format: "markdown" as const },
    { file: "documents/b.pdf", name: "b.pdf", kind: "binary" as const },
  ];

  it("binds the layer, then ingests text and binary through their own endpoints with linking", async () => {
    const calls = stubFetch();

    const result = await loadSampleGraph(
      instance,
      entryWith({ documents, linkIndexIds: ["asset-tags"] }),
      "/samples",
      { wipeFirst: false },
    );

    expect(result.documentsIngested).toBe(2);
    expect(result.chunksCreated).toBe(8); // 4 chunks per stubbed document

    // The binding must precede the first ingest, or ingestion answers 428. Match the ingest
    // routes precisely: "/document/binding/ensure" also contains "/document", so a loose
    // predicate would find the binding call and compare it against itself.
    const isIngest = (c: Recorded) =>
      c.method === "POST" && /\/document(\/text)?(\?|$)/.test(c.url);
    const bindIndex = calls.findIndex((c) => c.url.includes("/document/binding/ensure"));
    const firstIngest = calls.findIndex(isIngest);
    expect(bindIndex).toBeGreaterThanOrEqual(0);
    expect(firstIngest).toBeGreaterThan(bindIndex);
    expect(calls.filter(isIngest)).toHaveLength(2);

    const textCall = calls.find((c) => c.url.includes("/document/text"));
    expect(textCall?.body).toMatchObject({
      name: "a.md",
      format: "markdown",
      link: { indexIds: ["asset-tags"] },
    });

    // The binary goes multipart, and its link block rides as the linkJson form field.
    const formCall = calls.find(
      (c) => c.method === "POST" && /\/document$/.test(c.url.split("?")[0]),
    );
    expect(formCall?.body).toMatchObject({
      file: "file:b.pdf",
      name: "b.pdf",
      linkJson: JSON.stringify({ indexIds: ["asset-tags"] }),
    });
  });

  it("omits the link block when the sample declares no allowlist", async () => {
    const calls = stubFetch();
    await loadSampleGraph(instance, entryWith({ documents: [documents[0]] }), "/samples", {
      wipeFirst: false,
    });
    expect(calls.find((c) => c.url.includes("/document/text"))?.body).not.toHaveProperty("link");
  });

  it("fails with the unready role detail when the layer cannot bind", async () => {
    stubFetch({ bindingReady: false });
    await expect(
      loadSampleGraph(instance, entryWith({ documents }), "/samples", { wipeFirst: false }),
    ).rejects.toThrow(/could not be bound.*vector \(documents\)/s);
  });

  it("polls until indexed", async () => {
    stubFetch({ documentStates: ["processing", "processing", "indexed"] });
    vi.spyOn(globalThis, "setTimeout").mockImplementation(((fn: () => void) => {
      fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout);

    const result = await loadSampleGraph(
      instance,
      entryWith({ documents: [documents[0]] }),
      "/samples",
      { wipeFirst: false },
    );
    expect(result.documentsIngested).toBe(1);
  });

  it("gives up on a document that never reaches a terminal state", async () => {
    // Fake timers, because the loop's deadline is wall-clock: with setTimeout merely stubbed
    // synchronously, Date.now() never advances and a never-terminal document spins forever.
    vi.useFakeTimers();
    try {
      stubFetch({ documentStates: ["processing"] });
      const promise = loadSampleGraph(
        instance,
        entryWith({ documents: [documents[0]] }),
        "/samples",
        { wipeFirst: false },
      );
      const settled = expect(promise).rejects.toThrow(/did not finish within 300s/);
      // Advance past DOCUMENT_TIMEOUT_MS one poll at a time.
      for (let elapsed = 0; elapsed <= 301_000; elapsed += 1_500) {
        await vi.advanceTimersByTimeAsync(1_500);
      }
      await settled;
    } finally {
      vi.useRealTimers();
    }
  });

  it("tolerates transient status-read failures instead of discarding the whole load", async () => {
    // The load has already wiped and imported by this point, so one 502 from a proxy must not
    // throw it all away.
    stubFetch({ documentStates: ["boom", "boom", "indexed"] });
    vi.spyOn(globalThis, "setTimeout").mockImplementation(((fn: () => void) => {
      fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout);

    const result = await loadSampleGraph(
      instance,
      entryWith({ documents: [documents[0]] }),
      "/samples",
      { wipeFirst: false },
    );
    expect(result.documentsIngested).toBe(1);
  });

  it("gives up after too many consecutive status-read failures, naming what landed", async () => {
    stubFetch({ documentStates: ["boom"] });
    vi.spyOn(globalThis, "setTimeout").mockImplementation(((fn: () => void) => {
      fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout);

    await expect(
      loadSampleGraph(instance, entryWith({ documents: [documents[0]] }), "/samples", {
        wipeFirst: false,
      }),
    ).rejects.toThrow(/Lost track of 'a.md' after 3 failed status reads.*Knowledge screen/s);
  });

  it("does not mistake a vanished document for a sidecar timeout", async () => {
    stubFetch({ documentStates: ["gone"] });
    vi.spyOn(globalThis, "setTimeout").mockImplementation(((fn: () => void) => {
      fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    }) as typeof setTimeout);

    await expect(
      loadSampleGraph(instance, entryWith({ documents: [documents[0]] }), "/samples", {
        wipeFirst: false,
      }),
    ).rejects.toThrow(/no longer there \(deleted while it converted\?\)/);
  });

  it("fails with the server reason when a document ends up failed", async () => {
    stubFetch({ documentStates: ["failed"], documentError: "507 chunk ceiling reached" });
    await expect(
      loadSampleGraph(instance, entryWith({ documents: [documents[0]] }), "/samples", {
        wipeFirst: false,
      }),
    ).rejects.toThrow(/Ingesting 'a.md' failed: 507 chunk ceiling reached/);
  });
});

describe("loadSampleGraph: the document-free samples are untouched", () => {
  it("makes no /document or seeding call for a baked sample", async () => {
    const calls = stubFetch();

    const result = await loadSampleGraph(instance, entryWith(), "/samples", { wipeFirst: false });

    expect(calls.some((c) => c.url.includes("/document"))).toBe(false);
    expect(calls.some((c) => c.method === "PUT" && c.url.includes("/index/"))).toBe(false);
    expect(result).toMatchObject({
      verticesCreated: 2,
      edgesCreated: 1,
      tagsSeeded: 0,
      documentsIngested: 0,
      chunksCreated: 0,
    });
  });
});

describe("ingestionGate", () => {
  const status = (overrides: Partial<StatusREST>): StatusREST =>
    ({
      ingestion: { enabled: true, docling: { configured: true, reachable: true } },
      embedding: { enabled: true },
      nlp: { enabled: true, configured: true, reachable: true },
      ...overrides,
    }) as StatusREST;

  const withDocs = (kind: "text" | "binary") =>
    entryWith({ documents: [{ file: "d", name: "d", kind }] });

  it("is not-needed, and never blocking, for a sample without documents", () => {
    expect(ingestionGate(entryWith(), null)).toEqual({ kind: "not-needed", blocking: false });
  });

  it("blocks WITHOUT inventing a reason while /status has not resolved", () => {
    // The old behaviour claimed "ingestion is off, set F8_INGESTION=true" on first mount and for
    // any unreachable instance, sending the user to fix a setting that is probably already right.
    const gate = ingestionGate(withDocs("text"), null);
    expect(gate.kind).toBe("status-unknown");
    expect(gate.blocking).toBe(true);
    expect("detail" in gate && gate.detail).not.toMatch(/F8_INGESTION/);
  });

  it("blocks when the ingestion capability is off (every /document route 403s)", () => {
    const gate = ingestionGate(withDocs("text"), status({ ingestion: null }));
    expect(gate.kind).toBe("ingestion-off");
    expect(gate.blocking).toBe(true);
  });

  it("blocks when the embedding provider is off, since fused search is half the demo", () => {
    const gate = ingestionGate(withDocs("text"), status({ embedding: null }));
    expect(gate.kind).toBe("provider-off");
    expect(gate.blocking).toBe(true);
  });

  it("blocks on an unreachable docling ONLY when a document actually needs it", () => {
    const unreachable = status({
      ingestion: {
        enabled: true,
        docling: { configured: true, reachable: false },
      } as StatusREST["ingestion"],
    });
    expect(ingestionGate(withDocs("binary"), unreachable).kind).toBe("docling-unreachable");
    // A text-only sample skips the sidecar entirely, so it must still load.
    expect(ingestionGate(withDocs("text"), unreachable).kind).toBe("ready");
  });

  it("warns without blocking when NLP is off, because enrichment is additive", () => {
    const gate = ingestionGate(withDocs("text"), status({ nlp: null }));
    expect(gate.kind).toBe("nlp-off");
    expect(gate.blocking).toBe(false);
  });

  it("is ready when the whole environment is up", () => {
    expect(ingestionGate(withDocs("binary"), status({})).kind).toBe("ready");
  });
});
