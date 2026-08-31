// MIT License
//
// api-upload.test.ts
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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiUpload, wasCancelled } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";

/**
 * The upload transport (feature integration-file-transport). It is the one call in Studio that goes
 * through XMLHttpRequest rather than fetch, for one reason: fetch cannot report how far an upload
 * has got, and a multi-gigabyte send with no feedback is indistinguishable from a hang - which is
 * exactly what the operator who prompted the feature reported.
 *
 * Being the exception, it has to behave like everything else at its edges: the same URL scoping, the
 * same auth headers, the same `ApiError` for a status, and an abort that reads as a cancellation
 * rather than as a failure. Each of those is one test here.
 */

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

/** The last request this stub was given, so the assertions are about what went out. */
interface Sent {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: FormData | null;
}

let sent: Sent[] = [];
let live: StubXhr[] = [];

class StubXhr {
  private handlers: Record<string, Array<() => void>> = {};
  private uploadHandlers: Array<(event: ProgressEvent) => void> = [];
  private record: Sent = { method: "GET", url: "", headers: {}, body: null };

  status = 200;
  responseText = "null";

  readonly upload = {
    addEventListener: (_name: string, handler: (event: ProgressEvent) => void) => {
      this.uploadHandlers.push(handler);
    },
  };

  open(method: string, url: string) {
    this.record.method = method;
    this.record.url = url;
  }

  setRequestHeader(name: string, value: string) {
    this.record.headers[name] = value;
  }

  addEventListener(name: string, handler: () => void) {
    (this.handlers[name] ??= []).push(handler);
  }

  removeEventListener(name: string, handler: () => void) {
    this.handlers[name] = (this.handlers[name] ?? []).filter((h) => h !== handler);
  }

  send(body: FormData) {
    this.record.body = body;
    sent.push(this.record);
    live.push(this);
  }

  abort() {
    this.fire("abort");
  }

  /** Drives one of the events the real object would raise. */
  fire(name: string) {
    for (const handler of [...(this.handlers[name] ?? [])]) handler();
  }

  progress(loaded: number, total: number | null) {
    for (const handler of this.uploadHandlers) {
      handler({
        loaded,
        total: total ?? 0,
        lengthComputable: total !== null,
      } as ProgressEvent);
    }
  }
}

beforeEach(() => {
  sent = [];
  live = [];
  vi.stubGlobal("XMLHttpRequest", StubXhr);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function form(): FormData {
  const data = new FormData();
  data.append("job", "{}");
  return data;
}

describe("apiUpload", () => {
  it("posts the form to the scoped URL with the instance's credentials", async () => {
    const secured: InstanceConfig = {
      ...instance,
      namespace: "vehicles",
      auth: { kind: "apiKey", key: "k3y" },
    };

    const pending = apiUpload(secured, "/integrations/job", form());
    await Promise.resolve();
    live[0].fire("load");
    await pending;

    expect(sent[0].method).toBe("POST");
    // Namespace-scoped by default, exactly like apiRequest, so one transport does not quietly
    // address a different graph than the rest of the client.
    expect(sent[0].url).toBe("http://f8.test/ns/vehicles/integrations/job");
    expect(sent[0].headers.Authorization).toBe("Bearer k3y");
    // NO Content-Type: the browser writes it with the boundary, and one set here would send a
    // boundary nothing matches, leaving the server with no parts at all.
    expect(Object.keys(sent[0].headers)).not.toContain("Content-Type");
  });

  it("honours the fallen8 scope, so a Fallen-8-level route stays bare", async () => {
    const bound: InstanceConfig = { ...instance, namespace: "vehicles" };

    const pending = apiUpload(bound, "/integrations/job", form(), { scope: "fallen8" });
    await Promise.resolve();
    live[0].fire("load");
    await pending;

    expect(sent[0].url).toBe("http://f8.test/integrations/job");
  });

  it("reports progress, saying so when the total is unknown", async () => {
    const seen: Array<{ sent: number; total: number | null }> = [];

    const pending = apiUpload(instance, "/integrations/job", form(), {
      onProgress: (progress) => seen.push(progress),
    });
    await Promise.resolve();
    live[0].progress(512, 4096);
    live[0].progress(1024, null);
    live[0].fire("load");
    await pending;

    expect(seen).toEqual([
      { sent: 512, total: 4096 },
      // Null rather than 0: a caller that took a zero total literally would render a percentage of
      // nothing, and inventing a denominator is worse than admitting there is none.
      { sent: 1024, total: null },
    ]);
  });

  it("parses the answer, and reads an empty body as nothing rather than as a failure", async () => {
    const first = apiUpload<{ runId: string }>(instance, "/integrations/job", form());
    await Promise.resolve();
    live[0].responseText = '{"runId":"abc"}';
    live[0].fire("load");
    expect(await first).toEqual({ runId: "abc" });

    const second = apiUpload(instance, "/integrations/job", form());
    await Promise.resolve();
    live[1].status = 204;
    live[1].responseText = "";
    live[1].fire("load");
    expect(await second).toBeNull();
  });

  it("turns a status into the same ApiError every other call produces", async () => {
    const pending = apiUpload(instance, "/integrations/job", form());
    await Promise.resolve();
    live[0].status = 413;
    live[0].responseText = "Job body too large";
    live[0].fire("load");

    // The same class, so one ErrorBox and one 413 hint serve every transport rather than each
    // needing its own arm.
    await expect(pending).rejects.toBeInstanceOf(ApiError);
    await expect(pending).rejects.toMatchObject({ status: 413, body: "Job body too large" });
  });

  it("reports a transport failure as a TypeError, which is what fetch throws too", async () => {
    const pending = apiUpload(instance, "/integrations/job", form());
    await Promise.resolve();
    live[0].fire("error");

    // One error taxonomy across both transports: ErrorBox renders a TypeError as an unreachable
    // instance, which is what a failed connection is.
    await expect(pending).rejects.toBeInstanceOf(TypeError);
  });

  it("aborting rejects with something that reads as a cancellation, not a failure", async () => {
    const controller = new AbortController();
    const pending = apiUpload(instance, "/integrations/job", form(), {
      signal: controller.signal,
    });
    await Promise.resolve();

    controller.abort();

    await expect(pending).rejects.toSatisfy(wasCancelled);
  });

  it("stops listening to a signal once the request is done", async () => {
    const controller = new AbortController();
    const pending = apiUpload(instance, "/integrations/job", form(), {
      signal: controller.signal,
    });
    await Promise.resolve();
    live[0].fire("load");
    await pending;

    // Aborting AFTER the answer must not fire the abort handler and reject an already resolved
    // promise: an unhandled rejection is how a stray "cancelled" error appears minutes later.
    expect(() => controller.abort()).not.toThrow();
  });
});

describe("wasCancelled", () => {
  it("recognises an abort whatever class carries it", () => {
    expect(wasCancelled(new DOMException("stopped", "AbortError"))).toBe(true);
    // The plain-object case is not hypothetical: jsdom's DOMException does not extend Error, so a
    // guard written as `instanceof Error` passes in a browser and fails here - which is the worst
    // possible split, since here is where it is verified.
    expect(wasCancelled({ name: "AbortError" })).toBe(true);
    const named = new Error("stopped");
    named.name = "AbortError";
    expect(wasCancelled(named)).toBe(true);
  });

  it("does not swallow a real failure", () => {
    expect(wasCancelled(new TypeError("Failed to fetch"))).toBe(false);
    expect(wasCancelled(new ApiError(500, "/x", "boom"))).toBe(false);
    expect(wasCancelled(null)).toBe(false);
    expect(wasCancelled("AbortError")).toBe(false);
  });
});
