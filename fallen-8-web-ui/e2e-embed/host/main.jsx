// MIT License
//
// main.jsx (embed smoke fixture)
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

// The host application, written the way a real host portal would consume the artifact:
// everything comes through the package's export surface (the module and ./styles.css),
// react/react-dom resolve as the host's own copies (the package declares them as peers).

import { createRoot } from "react-dom/client";
import { mountStudio, F8GraphCanvas } from "fallen-8-web-ui";
import "fallen-8-web-ui/styles.css";
import "./host.css";

// The whole-app embed: memory history (the host owns the address bar), a prefixed storage
// namespace, an instance-locked NL-assist policy, and one theme token override the smoke
// test asserts lands on the scope root.
const studio = mountStudio(document.getElementById("studio-region"), {
  history: "memory",
  storageNamespace: "embed-smoke.",
  nlAssist: "instance-only",
  theme: { accent: "#e2001a" },
});

document.getElementById("host-unmount").addEventListener("click", () => studio.unmount());

// The component-level embed: the graph canvas alone, from literal data.
createRoot(document.getElementById("canvas-region")).render(
  <F8GraphCanvas
    nodes={{
      1: { id: 1, label: "turbine" },
      2: { id: 2, label: "site" },
    }}
    edges={{
      10: { id: 10, source: 1, target: 2, edgePropertyId: "locatedAt", label: null },
    }}
  />,
);
