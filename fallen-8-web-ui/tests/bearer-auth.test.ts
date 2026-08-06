// MIT License
//
// bearer-auth.test.ts
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
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { apiRequest, authHeaders, resolveAuthHeaders } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";

const SRC_DIR = join(dirname(fileURLToPath(import.meta.url)), "..", "src");

/**
 * The bearer auth arm (feature studio-embeddable): the token comes from a host-supplied
 * async provider and resolves per request at the transport choke points. The sync
 * authHeaders THROWS for it, so a call site that forgets resolveAuthHeaders fails loudly
 * instead of silently sending an unauthenticated request.
 */

function bearerInstance(getToken: () => Promise<string>): InstanceConfig {
  return { id: "host", name: "host", baseUrl: "", auth: { kind: "bearer", getToken } };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("resolveAuthHeaders", () => {
  it("awaits the host token provider per call", async () => {
    const getToken = vi.fn(async () => "tok-123");
    const instance = bearerInstance(getToken);

    await expect(resolveAuthHeaders(instance)).resolves.toEqual({
      Authorization: "Bearer tok-123",
    });
    await resolveAuthHeaders(instance);
    expect(getToken).toHaveBeenCalledTimes(2);
  });

  it("passes the sync kinds through unchanged", async () => {
    const apiKey: InstanceConfig = {
      id: "k",
      name: "k",
      baseUrl: "",
      auth: { kind: "apiKey", key: "s3cret" },
    };
    await expect(resolveAuthHeaders(apiKey)).resolves.toEqual({
      Authorization: "Bearer s3cret",
    });
    await expect(
      resolveAuthHeaders({ id: "n", name: "n", baseUrl: "", auth: { kind: "none" } }),
    ).resolves.toEqual({});
  });
});

describe("authHeaders with a bearer instance", () => {
  it("throws rather than returning empty headers, so a sync call site cannot send unauthenticated", () => {
    expect(() => authHeaders(bearerInstance(async () => "tok"))).toThrow(/resolveAuthHeaders/);
  });
});

describe("apiRequest with a bearer instance", () => {
  it("sends the resolved Authorization header", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => ({
      ok: true,
      status: 200,
      text: async () => "null",
    }));
    vi.stubGlobal("fetch", fetchMock);

    await apiRequest(bearerInstance(async () => "tok-999"), "/status", { scope: "fallen8" });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const init = fetchMock.mock.calls[0][1];
    expect((init?.headers as Record<string, string>).Authorization).toBe("Bearer tok-999");
  });

  it("rejects without sending the request when the host token provider fails", async () => {
    const fetchMock = vi.fn(async () => ({ ok: true, status: 200, text: async () => "null" }));
    vi.stubGlobal("fetch", fetchMock);
    const instance = bearerInstance(async () => {
      throw new Error("session expired");
    });

    await expect(apiRequest(instance, "/status", { scope: "fallen8" })).rejects.toThrow(
      "session expired",
    );
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe("transport call sites", () => {
  it("never build instance-authenticated requests with the sync authHeaders", () => {
    // A bearer token only resolves in resolveAuthHeaders, so a call site reaching for the
    // sync helper is the one way to reintroduce an unauthenticated request path (it now
    // throws for bearer, but failing the build beats failing at runtime). client.ts declares
    // both, so it is the only file allowed to name it.
    const offenders = readdirSync(SRC_DIR, { recursive: true, encoding: "utf8" })
      .filter((entry) => /\.tsx?$/.test(entry))
      .map((entry) => entry.replace(/\\/g, "/"))
      .filter((entry) => entry !== "api/client.ts")
      .filter((entry) => /\bauthHeaders\s*\(/.test(readFileSync(join(SRC_DIR, entry), "utf8")));

    expect(offenders).toEqual([]);
  });
});
