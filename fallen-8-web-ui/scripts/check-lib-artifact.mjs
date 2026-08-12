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
// because the invariants it checks fail SILENTLY otherwise: a Tailwind upgrade that emits a
// new global selector would style the host page, and a dependency reading process.env would
// throw only in a host that consumes the module without a bundler. Exit code is the verdict.

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const distLib = join(dirname(fileURLToPath(import.meta.url)), "..", "dist-lib");
const failures = [];

function requireFile(path, label) {
  if (!existsSync(path)) {
    failures.push(`${label} is missing: ${path}`);
    return null;
  }
  return readFileSync(path, "utf8");
}

// 1. The three artifact parts exist: entry module, stylesheet, entry declarations.
const entryJs = requireFile(join(distLib, "f8-studio.js"), "the entry module");
const css = requireFile(join(distLib, "f8-studio.css"), "the stylesheet");
requireFile(join(distLib, "types", "embed", "index.d.ts"), "the entry declarations");

// 2. No module in the artifact reads process.env (vite preserves it in lib mode unless
//    defined away; see vite.lib.config.ts).
if (entryJs !== null) {
  for (const file of readdirSync(distLib).filter((f) => f.endsWith(".js"))) {
    if (readFileSync(join(distLib, file), "utf8").includes("process.env.NODE_ENV")) {
      failures.push(`${file} still reads process.env.NODE_ENV; the define in vite.lib.config.ts did not reach it`);
    }
  }
}

// 3. Every selector in the stylesheet is scoped: it contains .f8-studio, or it is a keyframe
//    step. A bare html/body/:root/* selector means the artifact styles the HOST page.
if (css !== null) {
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, "");
  const stack = [];
  let buffer = "";
  const offenders = [];
  for (const ch of clean) {
    if (ch === "{") {
      const prologue = buffer.trim();
      const inKeyframes = stack.some((s) => s.startsWith("@") && s.includes("keyframes"));
      if (prologue !== "" && !prologue.startsWith("@") && !inKeyframes && !prologue.includes(".f8-studio")) {
        offenders.push(prologue);
      }
      stack.push(prologue);
      buffer = "";
    } else if (ch === "}") {
      stack.pop();
      buffer = "";
    } else if (ch === ";") {
      buffer = "";
    } else {
      buffer += ch;
    }
  }
  if (!clean.includes(".f8-studio")) {
    failures.push("the stylesheet never mentions .f8-studio; the scoping pass did not run");
  }
  for (const offender of offenders) {
    failures.push(`unscoped selector in f8-studio.css: "${offender}"`);
  }
}

if (failures.length > 0) {
  console.error("Library artifact check FAILED:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log("Library artifact check passed: entry, declarations, and a fully scoped stylesheet.");
