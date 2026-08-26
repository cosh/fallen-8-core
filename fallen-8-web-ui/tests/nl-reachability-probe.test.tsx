// MIT License
//
// nl-reachability-probe.test.tsx
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

import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderHook, waitFor } from "@testing-library/react";
import { DEFAULT_NL_CONFIG } from "../src/delegate/nl/config";
import { useReachabilityProbe } from "../src/delegate/nl/useNlRun";

/**
 * The reachability probe effect (nl-assist-ux FR-2) that NlAssistPanel and PluginNlAssistPanel
 * used to each hand-roll, near-verbatim, alongside the run lifecycle useNlRun already
 * consolidated. Extracted here as its own hook, which is also the first place it gets a unit
 * test in isolation - previously it was only reachable through a full panel render.
 */

const CUSTOM = {
  ...DEFAULT_NL_CONFIG,
  mode: "custom" as const,
  endpoint: "http://localhost:11434",
};

afterEach(() => vi.unstubAllGlobals());

describe("useReachabilityProbe", () => {
  // Both "reports nothing" cases assert the probe was never SENT, not merely that the state is
  // null: null is also the initial value of a probe that is in flight, so asserting the state
  // alone passes even with the guard removed. renderHook flushes the effect, and the probe's
  // fetch is issued synchronously inside it, so a plain assertion here is the strict one.
  it("reports nothing, and sends no probe, while not configured", () => {
    const fetchSpy = vi.fn(async () => new Response(null, { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    const { result } = renderHook(() => useReachabilityProbe(false, false, CUSTOM));

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(result.current).toBeNull();
  });

  it("sends no probe in instance mode - that reachability is the instance connection, shown on Connect", () => {
    const fetchSpy = vi.fn(async () => new Response(null, { status: 200 }));
    vi.stubGlobal("fetch", fetchSpy);

    const { result } = renderHook(() => useReachabilityProbe(true, true, CUSTOM));

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(result.current).toBeNull();
  });

  it("probes a configured custom endpoint and reports true when it answers", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 200 })));

    const { result } = renderHook(() => useReachabilityProbe(true, false, CUSTOM));

    await waitFor(() => expect(result.current).toBe(true));
  });

  it("reports false when the endpoint refuses", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 500 })));

    const { result } = renderHook(() => useReachabilityProbe(true, false, CUSTOM));

    await waitFor(() => expect(result.current).toBe(false));
  });
});

describe("the probe effect lives in one place", () => {
  const here = dirname(fileURLToPath(import.meta.url));
  const read = (relPath: string) => readFileSync(resolve(here, "..", relPath), "utf8");

  it.each([
    "src/delegate/nl/NlAssistPanel.tsx",
    "src/plugin/nl/PluginNlAssistPanel.tsx",
  ] as const)("%s calls the shared hook instead of re-deriving the probe effect", (relPath) => {
    const source = read(relPath);
    expect(source).toMatch(/useReachabilityProbe\(/);
    expect(source).not.toMatch(/probeEndpoint\(/);
  });
});
