// MIT License
//
// vite.lib.config.ts
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

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { Plugin as PostcssPlugin } from "postcss";

const rootDir = dirname(fileURLToPath(import.meta.url));

/**
 * The library artifact (feature studio-embeddable, phase 6): the src/embed/index.ts surface
 * as one ES module a host application bundles, with react/react-dom left to the host as peer
 * dependencies. `npm run build:lib` chains d.ts emission (tsconfig.lib.json), this build, and
 * scripts/check-lib-artifact.mjs, which fails the build on the invariants this config exists
 * to hold (fully scoped CSS, no process.env in the module). The standalone SPA build
 * (vite.config.ts) is untouched by everything in this file.
 */

const SCOPE = ".f8-studio";

/** Split a selector list on top-level commas only (commas inside :is()/:where() stay put). */
function splitSelectors(selector: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = "";
  for (const ch of selector) {
    if (ch === "(") depth += 1;
    else if (ch === ")") depth -= 1;
    if (ch === "," && depth === 0) {
      parts.push(current);
      current = "";
    } else {
      current += ch;
    }
  }
  parts.push(current);
  return parts.map((p) => p.trim()).filter((p) => p !== "");
}

/**
 * Rewrite one compound selector under the Studio scope root. The standalone stylesheet
 * deliberately styles the page (`:root` theme tokens, `html`/`body` chrome, Tailwind's
 * preflight on `*`); an artifact loaded into a host page may style ONLY its own subtree, so
 * every selector either targets the scope root or descends from it. Selectors already
 * carrying the scope (the primitives, the scope root itself) pass through unchanged, which
 * also preserves their specificity relative to the standalone build.
 */
function scopeSelector(part: string): string[] {
  if (part.includes(SCOPE)) return [part];
  if ([":root", ":host", "html", "body", "#root"].includes(part)) return [SCOPE];
  if (part === "*") return [SCOPE, `${SCOPE} *`];
  // Bare pseudo-elements (preflight's `::before`, `::backdrop`, ...) apply to the root's own
  // pseudo and to every descendant's.
  if (part.startsWith("::")) return [`${SCOPE}${part}`, `${SCOPE} ${part}`];
  // A compound anchored on html/body (e.g. `html:focus-within`) re-anchors on the root.
  const anchored = part.match(/^(html|body)(?![\w-])(.*)$/);
  if (anchored) return [`${SCOPE}${anchored[2]}`];
  return [`${SCOPE} ${part}`];
}

/**
 * Postcss pass over the fully expanded stylesheet (the tailwind plugin runs `enforce: "pre"`,
 * so by this stage preflight, theme tokens and utilities are plain rules). Keyframe steps
 * (`from`/`to`/percentages) are not element selectors and stay untouched.
 */
function scopeLibraryCss(): PostcssPlugin {
  return {
    postcssPlugin: "f8-scope-library-css",
    OnceExit(root) {
      root.walkRules((rule) => {
        for (let up = rule.parent; up && up.type !== "root"; up = up.parent) {
          if (up.type === "atrule" && /keyframes$/i.test((up as { name: string }).name)) return;
        }
        const scoped = splitSelectors(rule.selector).flatMap(scopeSelector);
        rule.selector = [...new Set(scoped)].join(", ");
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // No public/ copy into the artifact: the favicon and config.js are standalone-page concerns.
  publicDir: false,
  resolve: {
    alias: [
      // Lib mode cannot emit the worker as a separate served asset the way the SPA build
      // does, so the one monaco worker is inlined (a blob URL at runtime). SPA build
      // unaffected; the embed smoke test opens the editor to prove this keeps working.
      {
        find: /^monaco-editor\/esm\/vs\/editor\/editor\.worker\?worker$/,
        replacement: "monaco-editor/esm/vs/editor/editor.worker?worker&inline",
      },
    ],
  },
  define: {
    // Bundled deps (TanStack, zustand) read process.env.NODE_ENV, which vite deliberately
    // preserves in lib mode; a browser importing the artifact without a bundler would throw.
    "process.env.NODE_ENV": JSON.stringify("production"),
    // A host portal serves no /samples; the artifact reads the datasets from the repository's
    // raw mirror instead (sampleLoader.ts documents the override).
    "import.meta.env.VITE_F8_SAMPLES_BASE": JSON.stringify(
      "https://raw.githubusercontent.com/cosh/fallen-8-core/main/samples",
    ),
  },
  css: {
    postcss: {
      plugins: [scopeLibraryCss()],
    },
  },
  build: {
    lib: {
      entry: resolve(rootDir, "src/embed/index.ts"),
      formats: ["es"],
      fileName: "f8-studio",
      cssFileName: "f8-studio",
    },
    outDir: "dist-lib",
    rollupOptions: {
      external: [
        "react",
        "react-dom",
        "react-dom/client",
        "react/jsx-runtime",
        "react/jsx-dev-runtime",
      ],
    },
    chunkSizeWarningLimit: 4500, // monaco + sigma are intentionally bundled (self-contained)
  },
});
