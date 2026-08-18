// MIT License
//
// nl-run.test.tsx
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

import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_NL_CONFIG } from "../src/delegate/nl/config";
import { generateChat, NL_REQUEST_TIMEOUT_MS } from "../src/delegate/nl/generate";
import { useElapsedSeconds, useNlRun } from "../src/delegate/nl/useNlRun";
import type { InstanceConfig } from "../src/instances/types";

/**
 * The NL-assist run lifecycle: the client-side deadline in the transport, and the panel-side
 * abort/progress behaviour both NL panels share. These were previously uncovered - a slow or
 * hung model call had no client bound, and closing the editor orphaned the in-flight request.
 */

const INSTANCE: InstanceConfig = {
  id: "t",
  name: "t",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

/**
 * A fetch that never answers but honours the abort signal, i.e. a wedged model call.
 *
 * It must reject on an ALREADY-aborted signal too, not just on a later abort event: apiRequest
 * awaits the auth headers before calling fetch, so a cancellation can land before fetch is
 * reached, and real fetch rejects such a call immediately. A listener-only stub hangs there
 * instead, which looks exactly like a product bug and is not one.
 */
function hangingFetch(): ReturnType<typeof vi.fn> {
  return vi.fn(
    (_url: string, init?: RequestInit) =>
      new Promise((_resolve, reject) => {
        const fail = () => reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
        if (init?.signal?.aborted) {
          fail();
          return;
        }
        init?.signal?.addEventListener("abort", fail, { once: true });
      }),
  );
}

describe("client-side deadline on one model call", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("gives up on a wedged call, naming its own limit and not overclaiming", async () => {
    vi.stubGlobal("fetch", hangingFetch());
    const call = generateChat({ ...DEFAULT_NL_CONFIG, mode: "instance" }, INSTANCE, [
      { role: "user", content: "hi" },
    ]);
    const assertion = expect(call).rejects.toThrow(/within Studio's 10-minute limit/);

    await vi.advanceTimersByTimeAsync(NL_REQUEST_TIMEOUT_MS + 1000);
    await assertion;

    // It must NOT claim the request never completed: a chat budget raised above this ceiling is
    // cut off here while the server is still legitimately working, so that claim would be false.
    await expect(call).rejects.not.toThrow(/never completed/);
  });

  it("does NOT fire before its deadline", async () => {
    vi.stubGlobal("fetch", hangingFetch());
    let settled = false;
    const call = generateChat({ ...DEFAULT_NL_CONFIG, mode: "instance" }, INSTANCE, [
      { role: "user", content: "hi" },
    ]);
    void call.catch(() => {
      settled = true;
    });

    await vi.advanceTimersByTimeAsync(NL_REQUEST_TIMEOUT_MS - 1000);
    expect(settled).toBe(false);

    // Drain it so the rejection is observed and does not leak between tests.
    await vi.advanceTimersByTimeAsync(2000);
    await expect(call).rejects.toThrow();
  });

  it("clears its timer on success, so a fast call leaves no 10-minute timer behind", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () =>
          new Response(JSON.stringify({ content: "return (v) => true;", model: "m" }), {
            status: 200,
          }),
      ),
    );
    const { content } = await generateChat(
      { ...DEFAULT_NL_CONFIG, mode: "instance" },
      INSTANCE,
      [{ role: "user", content: "hi" }],
    );
    expect(content).toBe("return (v) => true;");
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe("useNlRun", () => {
  it("aborts the in-flight run when the panel unmounts, instead of orphaning it", () => {
    const { result, unmount } = renderHook(() => useNlRun());
    const controller = result.current.begin();
    expect(controller.signal.aborted).toBe(false);

    unmount();
    expect(controller.signal.aborted).toBe(true);
  });

  it("aborts a previous run when a new one begins, so two runs never write at once", () => {
    const { result } = renderHook(() => useNlRun());
    const first = result.current.begin();
    const second = result.current.begin();

    expect(first.signal.aborted).toBe(true);
    expect(second.signal.aborted).toBe(false);
  });

  it("cancel() aborts the current run and is safe with no run in flight", () => {
    const { result } = renderHook(() => useNlRun());
    expect(() => result.current.cancel()).not.toThrow();

    const controller = result.current.begin();
    result.current.cancel();
    expect(controller.signal.aborted).toBe(true);
  });
});

describe("useElapsedSeconds", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("counts whole seconds while active and resets when the run ends", () => {
    const { result, rerender } = renderHook(({ active }) => useElapsedSeconds(active), {
      initialProps: { active: true },
    });
    expect(result.current).toBe(0);

    act(() => void vi.advanceTimersByTime(3000));
    expect(result.current).toBe(3);

    rerender({ active: false });
    expect(result.current).toBe(0);
  });

  it("stays at zero while inactive", () => {
    const { result } = renderHook(({ active }) => useElapsedSeconds(active), {
      initialProps: { active: false },
    });
    act(() => void vi.advanceTimersByTime(5000));
    expect(result.current).toBe(0);
  });
});

describe("a caller abort is not the deadline (real timers: the abort needs no clock)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("does not run at all when handed an already-aborted signal", async () => {
    const fetchStub = hangingFetch();
    vi.stubGlobal("fetch", fetchStub);
    const controller = new AbortController();
    controller.abort();

    await expect(
      generateChat(
        { ...DEFAULT_NL_CONFIG, mode: "instance" },
        INSTANCE,
        [{ role: "user", content: "hi" }],
        controller.signal,
      ),
    ).rejects.toThrow();
    // The point of the fix: an already-aborted signal fires no abort event, so a forward-only
    // implementation would have issued the request and then hung. Assert the request is refused,
    // i.e. the signal handed to fetch is already aborted (fetch itself is what rejects it).
    expect(fetchStub).toHaveBeenCalledTimes(1);
    const passedSignal = (fetchStub.mock.calls[0][1] as RequestInit).signal;
    expect(passedSignal?.aborted).toBe(true);
  });

  it("rejects with the transport's own abort, never the deadline message", async () => {
    vi.stubGlobal("fetch", hangingFetch());
    const controller = new AbortController();
    const call = generateChat(
      { ...DEFAULT_NL_CONFIG, mode: "instance" },
      INSTANCE,
      [{ role: "user", content: "hi" }],
      controller.signal,
    );
    controller.abort();

    let message: string | null = null;
    try {
      await call;
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
    }

    expect(message).not.toBeNull();
    // The discriminator the panels depend on: they check their own controller's signal.aborted in
    // the catch and stay silent, so a Cancel must not arrive wearing the deadline's message.
    expect(message).not.toMatch(/No answer from the model/);
  });
});
