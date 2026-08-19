// MIT License
//
// check-lib-artifact.mjs
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

// The library artifact's tripwire, run as the last step of `npm run build:lib`. It exists
// because the invariants it checks fail SILENTLY otherwise: a selector styling the host
// page or a rule scoped into unmatchability changes nothing in the SPA, and a dependency
// reading process.env throws only in a host that consumes the module without a define.
// Exit code is the verdict. CSS is parsed with postcss - the same parser the scoping pass
// runs on - not a hand scanner, so braces inside quoted values or data URIs cannot desync
// the check.

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import postcss from "postcss";
import { SCOPE, UNMATCHABLE_SCOPED } from "./lib-scope.mjs";

const packageRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const distLib = join(packageRoot, "dist-lib");
const failures = [];

if (!existsSync(distLib)) {
  console.error(`Library artifact check FAILED: ${distLib} does not exist (run the build first).`);
  process.exit(1);
}

// 1. Every file the exports map promises actually exists. Driven BY the map rather than by
//    a hand-kept list, so a subpath added to package.json is checked without touching this
//    file - the map is the one home for what the package publishes.
const pkg = JSON.parse(readFileSync(join(packageRoot, "package.json"), "utf8"));
const exportTargets = new Set();
const collectTargets = (node) => {
  if (typeof node === "string") exportTargets.add(node);
  else if (node && typeof node === "object") Object.values(node).forEach(collectTargets);
};
collectTargets(pkg.exports ?? {});
for (const target of [...exportTargets].sort()) {
  if (!existsSync(join(packageRoot, target))) {
    failures.push(`the exports map promises ${target}, which does not exist`);
  }
}

const emitted = readdirSync(distLib);

// 2. No module in the artifact reads process.env (vite preserves it in lib mode unless
//    defined away; see vite.lib.config.ts), and no worker was emitted as a separate chunk
//    (every ?worker import must be inlined - an emitted worker file is unreachable from a
//    host origin).
for (const file of emitted.filter((f) => f.endsWith(".js"))) {
  if (readFileSync(join(distLib, file)).includes("process.env.NODE_ENV")) {
    failures.push(`${file} still reads process.env.NODE_ENV; the define in vite.lib.config.ts did not reach it`);
  }
}
for (const file of emitted.filter((f) => /worker/i.test(f))) {
  failures.push(`${file} looks like an emitted worker chunk; ?worker imports must be inlined in lib mode`);
}

// 3. The canvas subpath does not drag the app shell in. This is the whole point of the
//    second entry: a host that wants a graph must not bundle the code editor. Checked by
//    walking the emitted chunk graph from each entry (static AND dynamic relative imports -
//    a dynamically imported editor is still a chunk the host's bundler emits), then looking
//    for the editor's own markers. The shell entry is asserted to CONTAIN them too: without
//    that half, a marker that stopped appearing anywhere would turn this into a false green.
const EDITOR_MARKERS = ["MonacoEnvironment", "monaco-editor"];

function reachableChunks(entry) {
  const seen = new Set();
  const queue = [entry];
  while (queue.length > 0) {
    const file = queue.pop();
    if (seen.has(file) || !existsSync(join(distLib, file))) continue;
    seen.add(file);
    const code = readFileSync(join(distLib, file), "utf8");
    // Covers `from"./x.js"`, `import"./x.js"` and `import("./x.js")` alike; vite emits every
    // intra-artifact specifier as a flat "./name.js" beside the entry.
    for (const match of code.matchAll(/["'(]\.\/([\w.-]+\.js)["')]/g)) queue.push(match[1]);
  }
  return seen;
}

function markersIn(chunks) {
  const hits = [];
  for (const file of chunks) {
    const code = readFileSync(join(distLib, file), "utf8");
    for (const marker of EDITOR_MARKERS) {
      if (code.includes(marker)) hits.push(`${marker} in ${file}`);
    }
  }
  return hits;
}

const canvasHits = markersIn(reachableChunks("canvas.js"));
if (canvasHits.length > 0) {
  failures.push(
    `the canvas entry reaches the code editor, so a canvas-only host would bundle it: ${canvasHits.join(", ")}`,
  );
}
const shellHits = markersIn(reachableChunks("f8-studio.js"));
if (shellHits.length === 0) {
  failures.push(
    `no chunk reachable from f8-studio.js mentions any of ${EDITOR_MARKERS.join("/")}; the ` +
      "markers have gone stale, so the canvas-entry check above proves nothing",
  );
}

// 4. Every selector is scoped AND matchable. Two failure shapes, one per direction:
//    a selector without the scope styles the HOST page; a page-level selector rewritten
//    into a descendant position (`.f8-studio :root`, `.f8-studio html`, ...) can never
//    match, so its rule silently vanishes from embeds only.
const cssPath = join(distLib, "f8-studio.css");
if (existsSync(cssPath)) {
  const css = readFileSync(cssPath, "utf8");
  if (!css.includes(SCOPE)) {
    failures.push(`the stylesheet never mentions ${SCOPE}; the scoping pass did not run`);
  }
  postcss.parse(css).walkRules((rule) => {
    const parent = rule.parent;
    if (parent?.type === "atrule" && /keyframes$/i.test(parent.name)) return;
    for (const selector of rule.selectors) {
      if (!selector.includes(SCOPE)) {
        failures.push(`unscoped selector in f8-studio.css: "${selector}"`);
      } else if (UNMATCHABLE_SCOPED.test(selector)) {
        failures.push(`unmatchable scoped selector in f8-studio.css: "${selector}"`);
      }
    }
  });
}

if (failures.length > 0) {
  console.error("Library artifact check FAILED:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log("Library artifact check passed: entry, declarations, no emitted worker, and a fully scoped stylesheet.");
