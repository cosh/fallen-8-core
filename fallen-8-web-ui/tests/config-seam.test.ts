// MIT License
//
// config-seam.test.ts
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

import { afterEach, describe, expect, it } from "vitest";
import { configuredApiUrl } from "../src/instances/registry";

/**
 * The runtime config seam (feature standalone-ui): configuredApiUrl() reads the REST endpoint a
 * standalone Studio was pointed at via config.js (window.__F8_CONFIG__), defaulting to same-origin
 * and reusing normalizeBaseUrl so a trailing slash or stray whitespace in F8_API_URL cannot corrupt
 * request URLs. It is a function (not an inline const) precisely so this test can set the global and
 * observe the result.
 */
describe("configuredApiUrl", () => {
  afterEach(() => {
    delete window.__F8_CONFIG__;
  });

  it("defaults to same-origin when the global is absent", () => {
    expect(configuredApiUrl()).toBe("");
  });

  it("treats an empty apiUrl as same-origin", () => {
    window.__F8_CONFIG__ = { apiUrl: "" };
    expect(configuredApiUrl()).toBe("");
  });

  it("returns a plain origin unchanged", () => {
    window.__F8_CONFIG__ = { apiUrl: "http://localhost:8080" };
    expect(configuredApiUrl()).toBe("http://localhost:8080");
  });

  it("strips a trailing slash", () => {
    window.__F8_CONFIG__ = { apiUrl: "https://graph.example.com/" };
    expect(configuredApiUrl()).toBe("https://graph.example.com");
  });

  it("trims surrounding whitespace", () => {
    window.__F8_CONFIG__ = { apiUrl: "  https://graph.example.com  " };
    expect(configuredApiUrl()).toBe("https://graph.example.com");
  });
});
