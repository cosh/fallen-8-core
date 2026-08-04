// MIT License
//
// cors-hint.test.ts
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
import { isCrossOriginInstance } from "../src/instances/types";

/**
 * Cross-origin detection (feature standalone-ui): drives the Connect screen's CORS hint, which
 * distinguishes a likely missing AllowedCorsOrigins entry from a genuinely down data plane. The
 * same-origin all-in-one default ("") must never be flagged cross-origin.
 */
describe("isCrossOriginInstance", () => {
  it("treats same-origin ('') as not cross-origin", () => {
    expect(isCrossOriginInstance("")).toBe(false);
    expect(isCrossOriginInstance("/")).toBe(false);
  });

  it("treats the app's own origin (and a trailing-slash form) as not cross-origin", () => {
    expect(isCrossOriginInstance(window.location.origin)).toBe(false);
    expect(isCrossOriginInstance(`${window.location.origin}/`)).toBe(false);
  });

  it("flags a different host or port as cross-origin", () => {
    expect(isCrossOriginInstance("https://graph.example.com:9999")).toBe(true);
    expect(isCrossOriginInstance("http://another-host:8080")).toBe(true);
  });
});
