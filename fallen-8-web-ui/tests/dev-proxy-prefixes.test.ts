// MIT License
//
// dev-proxy-prefixes.test.ts
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
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

/**
 * The dev proxy allowlist in vite.config.ts against the routes the API client actually emits.
 * A missing family does not fail loudly at runtime: the request falls through to Vite's SPA
 * fallback, which answers index.html with 200, so the client dies in JSON.parse and the
 * namespace probe never sees the 404 it would degrade on. So the drift is pinned here instead.
 *
 * Both sides are read from source: the allowlist as a literal (importing vite.config.ts would
 * pull in the whole plugin chain for a string array) and the client's routes as the leading
 * path literals in src/api. A route assembled without a leading "/x" literal escapes the scan;
 * every call today writes one.
 */

const UI_DIR = join(dirname(fileURLToPath(import.meta.url)), "..");

/** src/api files that emit a URL: the endpoint list, the SSE feed, and scopedPath's /ns. */
const CLIENT_SOURCES = ["src/api/endpoints.ts", "src/api/changefeed.ts", "src/api/client.ts"];

function readSource(relative: string): string {
  return readFileSync(join(UI_DIR, relative), "utf8");
}

/** The allowlist as vite.config.ts declares it (entries stay raw: a "^" one is a RegExp). */
const proxyPrefixes: string[] = (() => {
  const body = /const API_PREFIXES = \[([\s\S]*?)\];/.exec(readSource("vite.config.ts"));
  return [...(body?.[1] ?? "").matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) =>
    // The file is TypeScript source: unescape the one escape it uses (\? inside a RegExp entry).
    m[1].replace(/\\\\/g, "\\"),
  );
})();

/** Vite's own matcher, so the test agrees with the dev server (doesProxyContextMatchUrl). */
function isProxied(url: string): boolean {
  return proxyPrefixes.some(
    (context) =>
      (context.startsWith("^") && new RegExp(context).test(url)) || url.startsWith(context),
  );
}

/** Every distinct root segment the client can put on the wire, e.g. "/vertex", "/ns". */
const clientRoots: string[] = [
  ...new Set(
    CLIENT_SOURCES.flatMap((file) =>
      [...readSource(file).matchAll(/["`](\/[A-Za-z][A-Za-z0-9]*)/g)].map((m) => m[1]),
    ),
  ),
].sort();

describe("dev proxy allowlist", () => {
  it("parses the allowlist and the client's routes (the scan itself must not go silent)", () => {
    expect(proxyPrefixes.length).toBeGreaterThan(20);
    expect(clientRoots.length).toBeGreaterThan(20);
    // The namespace prefix is the one every scoped call carries (client.ts scopedPath).
    expect(clientRoots).toContain("/ns");
    expect(clientRoots).toContain("/statistics");
  });

  it("proxies every client route family, with a subpath and with a query string", () => {
    for (const root of clientRoots) {
      expect(isProxied(root), `${root} is not proxied in dev`).toBe(true);
      // Real URLs carry ids and waitForCompletion; an over-anchored entry would miss them.
      expect(isProxied(`${root}/1/sub`), `${root}/1/sub is not proxied in dev`).toBe(true);
      expect(
        isProxied(`${root}?waitForCompletion=true`),
        `${root}?waitForCompletion=true is not proxied in dev`,
      ).toBe(true);
    }
  });

  it("namespace-scoped URLs are proxied whatever the family", () => {
    for (const root of clientRoots) {
      expect(isProxied(`/ns/default${root}`), `/ns/default${root} is not proxied`).toBe(true);
    }
    expect(isProxied("/ns/analytics%20lab/vertex/1")).toBe(true);
  });

  it("never steals a dev-server asset (the /config.js and /index.html traps)", () => {
    for (const asset of [
      "/config.js", // public/config.js: the runtime config the shell loads as a classic script
      "/index.html", // Vite's SPA entry, also the html-fallback rewrite target
      "/@vite/client",
      "/src/main.tsx",
      "/src/index.css",
      "/F8White.svg",
      "/samples/index.json",
      "/node_modules/.vite/deps/react.js",
      "/",
    ]) {
      expect(isProxied(asset), `${asset} must be served by Vite, not proxied`).toBe(false);
    }
  });

  it("carries no stale entry: each one still covers a route the client emits", () => {
    const stale = proxyPrefixes.filter(
      (context) =>
        !clientRoots.some(
          (root) =>
            (context.startsWith("^") && new RegExp(context).test(root)) ||
            root.startsWith(context),
        ),
    );
    expect(stale, "drop allowlist entries whose route family is gone").toEqual([]);
  });
});
