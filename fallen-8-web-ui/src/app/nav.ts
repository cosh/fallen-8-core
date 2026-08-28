// MIT License
//
// nav.ts
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

/**
 * The Studio navigation: the single source of truth for the shell's icon rail and for anything
 * keyed by section (e.g. the per-section help in lib/sectionHelp.ts). Connect, Save games and
 * Integrations are Fallen-8-level (flat routes); the rest operate on the ACTIVE NAMESPACE and
 * live under /q/{ns}/… (feature graph-namespaces).
 *
 * An entry may declare a `capability`, which the shell reads to HIDE it rather than disable it. Only
 * one does: an instance either has an integrations runtime or has nothing to say about integrations,
 * and a permanently disabled icon would advertise a deployable that is not there.
 */
export const NAV = [
  { leaf: "/", label: "Connect", icon: "◉", scoped: false },
  { leaf: "samples", label: "Samples", icon: "◈", scoped: true },
  { leaf: "/save-games", label: "Save games", icon: "⭯", scoped: false },
  { leaf: "browser", label: "Browser", icon: "☰", scoped: true },
  { leaf: "query", label: "Query", icon: "∴", scoped: true },
  { leaf: "indexes", label: "Indexes", icon: "⌗", scoped: true },
  // Traverse (feature studio-traverse-merge): path finding and the subgraph builder were
  // near-twin screens sharing the delegate editor and ONE stored-query library rendered as
  // two panels. They are one entry with three tabs; /q/{ns}/path and /q/{ns}/subgraphs
  // redirect onto the matching tab, so the old rail slots keep answering their bookmarks.
  { leaf: "traverse", label: "Traverse", icon: "↝", scoped: true },
  { leaf: "analytics", label: "Analytics", icon: "∑", scoped: true },
  { leaf: "plugins", label: "Plugins", icon: "⧉", scoped: true },
  { leaf: "canvas", label: "Canvas", icon: "❉", scoped: true },
  // Namespace-scoped: generation WRITES the active graph and the measurement reads it, so the
  // namespace is in the URL like every other scoped screen (it used to be a flat route that
  // silently generated into "default" whatever the switcher said).
  { leaf: "benchmarks", label: "Benchmark", icon: "◔", scoped: true },
  // Knowledge (feature semantic-layer): the semantic layer over the graph. Deliberately last,
  // after Benchmark - it is the "documents in, graph out" entry point, not a core-graph screen.
  { leaf: "knowledge", label: "Knowledge", icon: "▤", scoped: true },
  // Integrations (feature integrations): systems on your own network in, graph out. Last, next to
  // Knowledge, because both are data-in entry points rather than core-graph screens, and HIDDEN
  // unless the instance has a runtime to talk to.
  {
    leaf: "/integrations",
    label: "Integrations",
    icon: "⇄",
    scoped: false,
    capability: "integrations",
  },
] as const;

/** One navigation entry. */
export type NavItem = (typeof NAV)[number];

/** The capability an entry needs, or undefined when it always belongs in the rail. */
export function navCapability(item: NavItem): "integrations" | undefined {
  return "capability" in item ? item.capability : undefined;
}
