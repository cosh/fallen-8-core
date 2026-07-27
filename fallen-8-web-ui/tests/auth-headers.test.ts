// MIT License
//
// auth-headers.test.ts
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
import { authHeaders, buildUrl } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";

/** Lightweight auth (feature web-ui): bearer by default, named header opt-in. */
describe("instance auth headers", () => {
  const base: Omit<InstanceConfig, "auth"> = { id: "i", name: "i", baseUrl: "" };

  it("sends nothing for auth kind none", () => {
    expect(authHeaders({ ...base, auth: { kind: "none" } })).toEqual({});
  });

  it("sends Authorization: Bearer by default (Cognito-shaped seam)", () => {
    expect(authHeaders({ ...base, auth: { kind: "apiKey", key: "s3cret" } })).toEqual({
      Authorization: "Bearer s3cret",
    });
  });

  it("sends a named header when configured", () => {
    expect(
      authHeaders({ ...base, auth: { kind: "apiKey", key: "s3cret", header: "X-Api-Key" } }),
    ).toEqual({ "X-Api-Key": "s3cret" });
  });
});

describe("url building", () => {
  it("keeps routes root-level against the instance base", () => {
    expect(buildUrl("http://h:1", "/status")).toBe("http://h:1/status");
    expect(buildUrl("", "/graph", { maxElements: 50 })).toBe("/graph?maxElements=50");
  });

  it("drops undefined query values", () => {
    expect(buildUrl("", "/save", { waitForCompletion: true, savePath: undefined })).toBe(
      "/save?waitForCompletion=true",
    );
  });
});
