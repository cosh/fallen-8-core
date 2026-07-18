import { describe, expect, it, vi, afterEach } from "vitest";
import * as endpoints from "../src/api/endpoints";
import { ApiError } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";

/**
 * The raw-fetch bulk endpoints (exportBulk/importBulk) bypass apiRequest but must still turn a
 * non-ok response into an ApiError carrying status + the server body, via the shared throwIfNotOk
 * (client.ts). Pins that error path, including the import-into-non-empty-graph 409 the server returns.
 */

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("bulk endpoints surface server errors via throwIfNotOk", () => {
  it("importBulk rejects with an ApiError preserving status and body on a 409 (non-empty graph)", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("graph must be empty", { status: 409 })),
    );
    const err = await endpoints
      .importBulk(instance, new Blob(['{"type":"meta"}\n']))
      .catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 409, body: "graph must be empty" });
  });

  it("exportBulk rejects with an ApiError preserving status and body on a 500", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("internal error", { status: 500 })),
    );
    const err = await endpoints.exportBulk(instance).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 500, body: "internal error" });
  });
});
