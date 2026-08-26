// MIT License
//
// api-timeout.test.ts
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
import { ApiTimeoutError, apiRequest, startDeadline } from "../src/api/client";
import { getStatus } from "../src/api/endpoints";
import type { InstanceConfig } from "../src/instances/types";

/**
 * The request deadline (`RequestOptions.timeoutMs`).
 *
 * The failure it exists for is not a server that refuses - that already errored cleanly - but one
 * that ACCEPTS the connection and never answers. A `fetch` against it never settles, so the promise
 * stays pending for ever, react-query never reports an error, and every screen waiting on it renders
 * its loading state indefinitely. That is exactly how a dead IPv6 loopback forward on one published
 * port presented: "checking…" for ever, no error anywhere, against a server that was healthy and
 * answering on 127.0.0.1 the whole time.
 */

const INSTANCE: InstanceConfig = {
  id: "i-1",
  name: "local",
  baseUrl: "http://localhost:8080",
  auth: { kind: "none" },
};

/**
 * A fetch that accepts and never answers, which is the whole point.
 *
 * It rejects IMMEDIATELY on an already-aborted signal, because the platform does: a double that only
 * listened for a future abort left this test hanging for a reason that had nothing to do with the
 * code under test - `apiRequest` awaits its auth headers before starting the deadline, so a caller
 * that aborts synchronously has already aborted by the time the signal is attached.
 */
function hangingFetch(): typeof globalThis.fetch {
  return vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
    return new Promise<Response>((_resolve, reject) => {
      const aborted = () => reject(new DOMException("The operation was aborted.", "AbortError"));
      const signal = init?.signal;
      if (signal?.aborted) {
        aborted();
        return;
      }

      signal?.addEventListener("abort", aborted, { once: true });
    });
  }) as unknown as typeof globalThis.fetch;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("the request deadline", () => {
  it("turns a request that is never answered into a stated failure", async () => {
    vi.stubGlobal("fetch", hangingFetch());

    const pending = apiRequest(INSTANCE, "/status", { timeoutMs: 50 });

    const error = await pending.then(
      () => null,
      (e: unknown) => e,
    );

    expect(error).toBeInstanceOf(ApiTimeoutError);
    // The message has to name the address and the substitution that fixes it, because the whole
    // defect was that nobody could tell WHERE the request went.
    expect((error as ApiTimeoutError).message).toContain("localhost:8080");
    expect((error as ApiTimeoutError).message).toContain("127.0.0.1");
    expect((error as ApiTimeoutError).timeoutMs).toBe(50);
    expect((error as ApiTimeoutError).status).toBe(0);
  });

  it("leaves a request with no deadline pending, so a long operation is never cut off", async () => {
    vi.stubGlobal("fetch", hangingFetch());

    let settled = false;
    void apiRequest(INSTANCE, "/integrations/job", { method: "POST", body: {} }).then(
      () => (settled = true),
      () => (settled = true),
    );

    await new Promise((resolve) => setTimeout(resolve, 100));

    // A job run over a 100 MiB extract takes half a minute of real time; a blanket deadline would
    // abort exactly the operations this API exists for.
    expect(settled).toBe(false);
  });

  it("rethrows the CALLER's cancellation untouched, so a navigation is not reported as a timeout", async () => {
    vi.stubGlobal("fetch", hangingFetch());

    const controller = new AbortController();
    const pending = apiRequest(INSTANCE, "/status", {
      signal: controller.signal,
      timeoutMs: 60_000,
    });

    controller.abort();

    const error = await pending.then(
      () => null,
      (e: unknown) => e,
    );

    // react-query aborts on unmount and on a superseded query. Reporting those as "the server did
    // not answer" would put a red unreachable state on the screen every time somebody navigates.
    expect(error).not.toBeInstanceOf(ApiTimeoutError);
    expect((error as DOMException).name).toBe("AbortError");
  });

  it("gives the reachability probe a deadline, since that is the one that renders a spinner", async () => {
    vi.stubGlobal("fetch", hangingFetch());
    // Fake timers, so the probe's real deadline is asserted without spending it: waiting it out
    // would put ten seconds of sleep into every run of this suite.
    vi.useFakeTimers();

    const pending = getStatus(INSTANCE).then(
      () => null,
      (e: unknown) => e,
    );

    // Far past any deadline a reachability probe could sensibly carry. The assertion is that ONE
    // exists, not what it is: pinning the number here would make it a fact in two places.
    await vi.advanceTimersByTimeAsync(60_000);

    const error = await pending;
    expect(error).toBeInstanceOf(ApiTimeoutError);
    expect((error as ApiTimeoutError).timeoutMs).toBeGreaterThan(0);
  });
});

describe("startDeadline is exported for reuse by other deadline-bound callers", () => {
  it("is callable directly, not only reachable through apiRequest", () => {
    const deadline = startDeadline(undefined, 1000);
    expect(deadline.signal).toBeInstanceOf(AbortSignal);
    expect(deadline.expired).toBe(false);
    deadline.done();
  });

  it("passes the caller's own signal through untouched when there is no timeout", () => {
    const controller = new AbortController();
    const deadline = startDeadline(controller.signal, undefined);
    expect(deadline.signal).toBe(controller.signal);
    deadline.done();
  });
});
