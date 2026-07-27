// MIT License
//
// nl-feedback.test.ts
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

import { describe, expect, it, vi } from "vitest";
import { downloadText, toTrainingJsonl, type TrainingExample } from "../src/delegate/nl/feedback";

/**
 * FL-2 of the nl-assist-feedback-loop feature: the local, opt-in capture format and its
 * download. The load-bearing property is that capturing NEVER touches the network - the
 * example leaves the machine only when the operator moves the downloaded file.
 */

const example = (over: Partial<TrainingExample> = {}): TrainingExample => ({
  delegateKind: "VertexFilter",
  intent: "only persons",
  fragment: 'return (v) => v.Label == "person";',
  verdict: "up",
  ts: 1234,
  ...over,
});

describe("training-example capture (FL-2)", () => {
  it("emits one JSON object per line; an empty set is the empty string", () => {
    expect(toTrainingJsonl([])).toBe("");

    const jsonl = toTrainingJsonl([example()]);
    expect(jsonl.endsWith("\n")).toBe(true);
    expect(jsonl.trimEnd().split("\n")).toHaveLength(1);
    expect(JSON.parse(jsonl.trim())).toEqual({
      delegateKind: "VertexFilter",
      intent: "only persons",
      fragment: 'return (v) => v.Label == "person";',
      verdict: "up",
      ts: 1234,
    });
  });

  it("emits one line per example and preserves each verdict", () => {
    const lines = toTrainingJsonl([example(), example({ verdict: "down", intent: "cars" })])
      .trimEnd()
      .split("\n");
    expect(lines).toHaveLength(2);
    expect(JSON.parse(lines[0]).verdict).toBe("up");
    expect(JSON.parse(lines[1])).toMatchObject({ verdict: "down", intent: "cars" });
  });

  it("downloadText stays local: a blob + link click, never a network call", () => {
    // jsdom implements neither URL.createObjectURL nor a navigating click, so DEFINE the
    // stubs (spyOn needs an existing prop) and restore them afterwards.
    const created: Blob[] = [];
    const original = {
      create: URL.createObjectURL,
      revoke: URL.revokeObjectURL,
      fetch: globalThis.fetch,
    };
    URL.createObjectURL = vi.fn((blob: Blob) => {
      created.push(blob);
      return "blob:mock";
    }) as typeof URL.createObjectURL;
    URL.revokeObjectURL = vi.fn() as typeof URL.revokeObjectURL;
    globalThis.fetch = vi.fn() as typeof globalThis.fetch;
    const clickSpy = vi.spyOn(HTMLElement.prototype, "click").mockImplementation(() => {});
    try {
      downloadText("f8-training.jsonl", toTrainingJsonl([example()]));

      expect(URL.createObjectURL).toHaveBeenCalledTimes(1);
      expect(clickSpy).toHaveBeenCalledTimes(1);
      expect(URL.revokeObjectURL).toHaveBeenCalledTimes(1);
      expect(globalThis.fetch).not.toHaveBeenCalled();
      expect(created).toHaveLength(1);
    } finally {
      URL.createObjectURL = original.create;
      URL.revokeObjectURL = original.revoke;
      globalThis.fetch = original.fetch;
      clickSpy.mockRestore();
    }
  });
});
