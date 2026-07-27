// MIT License
//
// sample-loader.test.ts
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

import { describe, expect, it } from "vitest";
import { embeddingGate } from "../src/lib/sampleLoader";
import { normalizeRepo } from "../src/components/SampleGraphsPanel";
import type { SampleEmbeddingInfo } from "../src/lib/samples";
import type { StatusREST } from "../src/api/types";

/**
 * The loader's decision logic (feature sample-graphs): the embedding gate tells the user
 * exactly what works on THIS instance (vector scan always; text-in only with a matching
 * provider), and repo normalization accepts the forms a user actually types.
 */
describe("embeddingGate", () => {
  const embedding: SampleEmbeddingInfo = {
    name: "default",
    model: "bge-m3#1024#Cosine",
    dimension: 1024,
    metric: "Cosine",
  };
  const status = (embed: Partial<StatusREST["embedding"]> | null): StatusREST =>
    ({ embedding: embed as StatusREST["embedding"] }) as StatusREST;

  it("is not-embedded for a dataset without vectors", () => {
    expect(embeddingGate(null, status({ enabled: true }))).toEqual({ kind: "not-embedded" });
  });

  it("is provider-off when the instance has no/disabled provider", () => {
    expect(embeddingGate(embedding, status(null)).kind).toBe("provider-off");
    expect(embeddingGate(embedding, status({ enabled: false })).kind).toBe("provider-off");
  });

  it("is ready when the provider identity matches the baked vectors", () => {
    expect(
      embeddingGate(embedding, status({ enabled: true, modelName: "bge-m3", dimension: 1024 })).kind,
    ).toBe("ready");
  });

  it("flags a dimension or model mismatch (text-in would 409)", () => {
    expect(
      embeddingGate(embedding, status({ enabled: true, modelName: "bge-m3", dimension: 384 })).kind,
    ).toBe("mismatch");
    expect(
      embeddingGate(embedding, status({ enabled: true, modelName: "nomic-embed", dimension: 1024 })).kind,
    ).toBe("mismatch");
  });
});

describe("normalizeRepo", () => {
  it("accepts owner/repo and full GitHub URLs", () => {
    expect(normalizeRepo("cosh/fallen-8-core")).toBe("cosh/fallen-8-core");
    expect(normalizeRepo("  cosh/fallen-8-core  ")).toBe("cosh/fallen-8-core");
    expect(normalizeRepo("https://github.com/cosh/fallen-8-core")).toBe("cosh/fallen-8-core");
    expect(normalizeRepo("https://github.com/cosh/fallen-8-core.git")).toBe("cosh/fallen-8-core");
    expect(normalizeRepo("github.com/facebook/react/")).toBe("facebook/react");
    // Trailing slash is stripped BEFORE .git, so a .git/ suffix still resolves.
    expect(normalizeRepo("https://github.com/cosh/fallen-8-core.git/")).toBe("cosh/fallen-8-core");
  });

  it("rejects garbage and partial input", () => {
    expect(normalizeRepo("")).toBeNull();
    expect(normalizeRepo("just-a-name")).toBeNull();
    expect(normalizeRepo("too/many/parts")).toBeNull();
  });
});
