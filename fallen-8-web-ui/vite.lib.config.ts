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
import type { AtRule, Plugin as PostcssPlugin } from "postcss";
import { OUTERMOST_SCOPE, PAGE_LEVEL_ANCHOR, SCOPE } from "./scripts/lib-scope.mjs";
import pkg from "./package.json";

const rootDir = dirname(fileURLToPath(import.meta.url));

/** What is external is exactly what package.json declares as peers - one authority. */
const peers = Object.keys(pkg.peerDependencies ?? {});

/**
 * The library artifact (feature studio-embeddable, phase 6): the src/embed/index.ts surface
 * as one ES module a host application bundles, with react/react-dom left to the host as peer
 * dependencies. `npm run build:lib` chains d.ts emission (tsconfig.lib.json), this build, and
 * scripts/check-lib-artifact.mjs, which fails the build on the invariants this config exists
 * to hold (fully scoped CSS, no process.env in the module). The standalone SPA build
 * (vite.config.ts) is untouched by everything in this file.
 */

/**
 * Rewrite one compound selector under the Studio scope root. The standalone stylesheet
 * deliberately styles the page (`:root` theme tokens, `html`/`body` chrome, Tailwind's
 * preflight on `*`); an artifact loaded into a host page may style ONLY its own subtree, so
 * every selector either targets the scope root or descends from it. Selectors already
 * carrying the scope (the primitives, the scope root itself) pass through unchanged, which
 * also preserves their specificity relative to the standalone build. Page-level selectors
 * re-anchor on the OUTERMOST scope root only, so a nested root (F8GraphCanvas inside the
 * Studio tree) inherits an ancestor's inline theme overrides instead of re-declaring stock
 * defaults on itself. A page-level form this pass does not recognize FAILS THE BUILD: the
 * fallthrough would produce `.f8-studio :root ...`, a descendant selector that can never
 * match, and the rule would silently vanish from embeds only.
 */
function scopeSelector(part: string): string[] {
  if (part.includes(SCOPE)) return [part];
  if (part === "*") return [SCOPE, `${SCOPE} *`];
  // Bare pseudo-elements (preflight's `::before`, `::backdrop`, ...) apply to the root's own
  // pseudo and to every descendant's.
  if (part.startsWith("::")) return [`${SCOPE}${part}`, `${SCOPE} ${part}`];
  const anchored = part.match(PAGE_LEVEL_ANCHOR);
  if (anchored) {
    const rest = anchored[2];
    // `html`/`body`/`:root`/... alone, or a compound (`html.dark`, `:root[data-theme]`):
    // re-anchor on the outermost scope root.
    if (rest === "") return [OUTERMOST_SCOPE];
    if (!/^[\s>+~]/.test(rest)) return [`${OUTERMOST_SCOPE}${rest}`];
    // A page-level ANCESTOR with descendants (`html body`, `#root > .x`) has no faithful
    // in-scope rewrite; the fallthrough would be a selector that never matches, and the
    // rule would silently vanish from embeds only - so the build fails instead.
    throw new Error(
      `f8-scope-library-css: page-level ancestor selector "${part}" has no in-scope ` +
        "rewrite - restructure the rule, or extend scopeSelector (scripts/lib-scope.mjs " +
        "owns the recognized forms).",
    );
  }
  return [`${SCOPE} ${part}`];
}

/**
 * Postcss pass over the fully expanded stylesheet (the tailwind plugin runs `enforce: "pre"`,
 * so by this stage preflight, theme tokens and utilities are plain rules). Keyframe steps
 * (`from`/`to`/percentages) are not element selectors and stay untouched; a step's parent is
 * always the @keyframes at-rule itself. `rule.selectors` is postcss's own quote- and
 * paren-aware selector-list split, so `[title="a, b"]` survives.
 */
function scopeLibraryCss(): PostcssPlugin {
  return {
    postcssPlugin: "f8-scope-library-css",
    OnceExit(root) {
      root.walkRules((rule) => {
        const parent = rule.parent;
        if (parent?.type === "atrule" && /keyframes$/i.test((parent as AtRule).name)) return;
        rule.selectors = [...new Set(rule.selectors.flatMap(scopeSelector))];
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
      // Lib mode cannot emit a worker as a separately served asset the way the SPA build
      // does, so EVERY `?worker` import is inlined (a blob URL at runtime) - keyed on the
      // suffix, not one specifier, so a second worker added later inherits the rule. SPA
      // build unaffected; the embed smoke opens the editor and asserts a live blob worker,
      // and the artifact check fails on any emitted worker chunk.
      { find: /\?worker$/, replacement: "?worker&inline" },
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
      // Two entries, so a canvas-only host does not bundle the app shell (Monaco above all).
      // The file name comes from the key, which is what the exports map points at; the
      // artifact check asserts the editor never leaks into the canvas entry's chunk graph.
      entry: {
        "f8-studio": resolve(rootDir, "src/embed/index.ts"),
        canvas: resolve(rootDir, "src/embed/canvas.ts"),
      },
      formats: ["es"],
      cssFileName: "f8-studio",
    },
    outDir: "dist-lib",
    rollupOptions: {
      // Peers and their subpaths (react-dom/client, react/jsx-runtime, ...): a hand-kept
      // list once missed a subpath, which bundles a second React copy and throws hook
      // errors only in hosts.
      external: (id) => peers.some((p) => id === p || id.startsWith(`${p}/`)),
    },
    chunkSizeWarningLimit: 4500, // monaco + sigma are intentionally bundled (self-contained)
  },
});
