// MIT License
//
// model-provenance.test.ts
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
import { chatAmbientLabel, embeddingStamp } from "../src/lib/modelProvenance";
import type { EmbeddingProviderStatsREST } from "../src/api/types";

/**
 * The two provenance labels (feature model-providers). What is pinned here is that neither
 * function ever produces a plausible-looking name it cannot know: every absent input maps to a
 * sentence saying the value is absent.
 */

const PROVIDER: EmbeddingProviderStatsREST = {
  enabled: true,
  backend: "Ollama",
  modelName: "bge-m3",
  modelVersion: "",
  dimension: 1024,
  intendedMetric: "Cosine",
  loaded: true,
};

describe("chatAmbientLabel", () => {
  it("pairs the backend with the model when the server reports both", () => {
    expect(
      chatAmbientLabel({
        enabled: true,
        backend: "Anthropic",
        model: "claude-opus-5",
        loaded: false,
      }),
    ).toBe("Anthropic · claude-opus-5");
  });

  it("distinguishes 'not answered yet' from 'chat is off'", () => {
    expect(chatAmbientLabel(undefined)).toBe("checking…");
    expect(chatAmbientLabel(null)).toBe("checking…");
    expect(
      chatAmbientLabel({ enabled: false, backend: "Ollama", model: "phi4-f8-mini", loaded: false }),
    ).toBe("chat is off on this instance");
  });

  it("says the backend was not reported rather than guessing the local sidecar", () => {
    const label = chatAmbientLabel({
      enabled: true,
      backend: null,
      model: "phi4-f8-mini",
      loaded: true,
    });
    expect(label).toBe("backend not reported by this server");
    expect(label).not.toContain("Ollama");
  });

  it("names the backend alone when the model is missing, with no dangling separator", () => {
    expect(
      chatAmbientLabel({ enabled: true, backend: "OpenAI", model: null, loaded: false }),
    ).toBe("OpenAI");
  });
});

describe("embeddingStamp", () => {
  it("is the model name, with the version appended only when there is one", () => {
    expect(embeddingStamp(PROVIDER)).toBe("bge-m3");
    expect(embeddingStamp({ ...PROVIDER, modelVersion: "v2" })).toBe("bge-m3@v2");
    expect(embeddingStamp({ ...PROVIDER, modelVersion: null })).toBe("bge-m3");
  });

  it("falls back to the dimension, the one property of the vector space still known", () => {
    expect(embeddingStamp({ ...PROVIDER, modelName: null })).toBe("1024d");
    // An empty name is as unusable as a missing one, and must not render as "@v2" alone.
    expect(embeddingStamp({ ...PROVIDER, modelName: "", modelVersion: "v2" })).toBe("1024d");
  });
});
