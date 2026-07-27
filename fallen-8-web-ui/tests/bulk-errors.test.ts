// MIT License
//
// bulk-errors.test.ts
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
