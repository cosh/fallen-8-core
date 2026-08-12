// MIT License
//
// vite.config.js (embed smoke fixture)
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

// A deliberately plain consumer: no react plugin (main.jsx uses automatic JSX via esbuild),
// no aliases, no tailwind - if the artifact needs anything beyond a stock bundler and its
// peer deps, this build fails, which is the point of the fixture.
export default defineConfig({
  resolve: {
    // The file: dependency is a SYMLINK into the repo, so the artifact's externalized
    // "react" would resolve to fallen-8-web-ui/node_modules/react while the fixture uses
    // its own copy - two Reacts, invalid-hook-call (#321). A registry install cannot hit
    // this (the package ships dist-lib only, no nested node_modules); dedupe compensates
    // for the symlinked test topology, nothing more.
    dedupe: ["react", "react-dom"],
  },
  esbuild: {
    jsx: "automatic",
  },
  build: {
    chunkSizeWarningLimit: 8000, // the artifact bundles monaco + sigma by design
  },
});
