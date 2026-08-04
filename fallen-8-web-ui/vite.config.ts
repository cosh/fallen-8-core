// MIT License
//
// vite.config.ts
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

import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { cpSync, existsSync, readFileSync, statSync } from "node:fs";
import { dirname, extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const rootDir = dirname(fileURLToPath(import.meta.url));

// Fallen-8 REST routes are root-level (see features/done/web-ui/spec.md §5). In dev the app is
// served by Vite, so requests against the same-origin "local" instance (baseUrl "") are
// proxied to a locally running fallen-8-core-apiApp. In production the SPA is served by the
// apiApp itself (see Program.cs, gap G-1) and no proxy is involved.
const API_PREFIXES = [
  "/status",
  "/graph",
  "/vertex",
  "/edge",
  "/graphelement",
  "/scan",
  "/path",
  "/subgraph",
  "/index",
  "/delegates",
  "/save",
  "/load",
  "/trim",
  "/tabularasa",
  "/generate",
  "/benchmark",
  "/plugin",
  "/changefeed",
];

const API_TARGET = process.env.F8_API_URL ?? "http://localhost:5000";

/**
 * The sample datasets (feature sample-graphs) live in the repo-root samples/ dir. This plugin
 * makes them available SAME-ORIGIN at /samples: it serves them from disk in dev and copies them
 * into the build output so the apiApp serves them from wwwroot. Result: the gallery shows the
 * samples the app was built with, so a newly added sample appears on rebuild without waiting for
 * a GitHub round-trip, and it works offline. (VITE_F8_SAMPLES_BASE still overrides the base to a
 * remote mirror or a fork; see sampleLoader.ts.)
 */
const SAMPLES_DIR = resolve(rootDir, "..", "samples");
const SAMPLE_CONTENT_TYPES: Record<string, string> = {
  ".json": "application/json",
  ".jsonl": "application/x-ndjson",
  // samples/documents/: the files the wind-farm sample ingests (feature knowledge-demo).
  ".md": "text/markdown",
  ".pdf": "application/pdf",
  ".xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
};

/**
 * What may ship from samples/ into the bundle. An ALLOWLIST on purpose: authoring aids live in
 * that tree next to the assets (a generator script, a README, a Python cache), and with a
 * recursive copy a denylist means anything new lands in the image by default. Extensions, not
 * paths, so adding a dataset needs no change here.
 */
const SHIPPABLE_SAMPLE_FILE = /\.(?:json|jsonl|md|pdf|xlsx)$/i;
/** The one .md that is an authoring aid rather than an ingestable asset. */
const SAMPLE_README = /[\\/]README\.md$/i;

function serveSamples(): Plugin {
  return {
    name: "f8-serve-samples",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = req.url?.split("?")[0];
        if (!url || !url.startsWith("/samples/")) return next();
        const file = resolve(SAMPLES_DIR, url.slice("/samples/".length));
        // isFile, not exists: samples/ now has a documents/ subdirectory, and readFileSync on a
        // directory throws EISDIR (a dev-server 500 instead of a clean 404).
        if (!file.startsWith(SAMPLES_DIR) || !existsSync(file) || !statSync(file).isFile()) {
          return next();
        }
        res.setHeader("Content-Type", SAMPLE_CONTENT_TYPES[extname(file)] ?? "application/octet-stream");
        res.end(readFileSync(file));
      });
    },
    writeBundle(options) {
      if (!options.dir || !existsSync(SAMPLES_DIR)) return;
      // Recursive: samples/ has a documents/ subdirectory (the files the wind-farm sample
      // ingests), and a flat copy throws EPERM on a directory entry. The authoring files that
      // live next to those documents are NOT runtime assets, so they stay out of the image.
      cpSync(SAMPLES_DIR, join(options.dir, "samples"), {
        recursive: true,
        // Directories must return true or their subtree is never walked.
        filter: (source) =>
          statSync(source).isDirectory() ||
          (SHIPPABLE_SAMPLE_FILE.test(source) && !SAMPLE_README.test(source)),
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), serveSamples()],
  server: {
    proxy: Object.fromEntries(
      API_PREFIXES.map((prefix) => [prefix, { target: API_TARGET, changeOrigin: true }]),
    ),
  },
  build: {
    chunkSizeWarningLimit: 4500, // monaco + sigma are intentionally bundled (self-contained)
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./tests/setup.ts"],
    include: ["tests/**/*.test.{ts,tsx}"],
    globals: true,
  },
});
