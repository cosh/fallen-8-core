// MIT License
//
// sectionHelp.ts
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
 * Per-section "How does this work?" help (feature studio-section-help): the ONE home for
 * mapping each Studio main section to the 1-3 docs pages that explain it. Keys are the same
 * `leaf` strings used by the NAV array in app/AppShell.tsx; the shell renders <SectionHelp/>
 * once, keyed by the active leaf. The explanation of every feature lives on its docs page -
 * this module only wires a section to the pages, it does not re-narrate them (one-home rule).
 *
 * Blurbs mirror each page's frontmatter `description` in short form. A vitest drift guard
 * asserts every `slug` here resolves to a real docs/src/content/docs/<slug>.(md|mdx) file, and
 * a coverage test asserts every NAV leaf has an entry here.
 */

/** Published Starlight docs origin. The single home for the docs URL, shared with the
 *  top-bar "docs" pill in AppShell so there is one origin constant, not two. */
export const DOCS_BASE = "https://cosh.github.io/fallen-8-core/";

/** Absolute URL of a docs page from its slug (e.g. "path-finding" -> ".../path-finding/"). */
export const docUrl = (slug: string): string => `${DOCS_BASE}${slug}/`;

/** One row in a section's help popover: a docs page, its title, and a one-line blurb. */
export interface SectionDocLink {
  /** Docs page slug, e.g. "path-finding" (NOT a full URL). Resolved via docUrl. */
  slug: string;
  /** Page title shown as the row label. */
  title: string;
  /** One-line description (mirrors the page's frontmatter description in short form). */
  blurb: string;
}

/** The help content for one Studio section: a heading plus 1-3 doc links, primary first. */
export interface SectionHelpEntry {
  /** Popover heading, e.g. "How path finding works". Also the button's title tooltip. */
  heading: string;
  /** Ordered primary-first, length 1..3 (enforced by test). */
  links: SectionDocLink[];
}

/**
 * Keyed by the SAME leaf strings as AppShell's NAV array. Flat/unscoped sections carry a
 * leading slash ("/", "/save-games", "/integrations"); scoped sections are bare leaves.
 */
export const SECTION_HELP: Record<string, SectionHelpEntry> = {
  "/": {
    heading: "How connecting works",
    links: [
      { slug: "running", title: "Running Fallen-8", blurb: "Every way to launch the engine, API, Studio, and model sidecar." },
      { slug: "standalone-ui", title: "Standalone F8 Studio", blurb: "Deploy Studio as its own container pointed at any REST endpoint." },
      { slug: "security", title: "Security", blurb: "The optional all-or-nothing API key; set one before exposing the service." },
    ],
  },
  dashboard: {
    heading: "How the dashboard works",
    links: [
      { slug: "studio", title: "F8 Studio", blurb: "The browser workbench: browse, query, visualize, and author C# delegates." },
      { slug: "observability", title: "Observability", blurb: "Metrics, traces, a graph-shape snapshot, and health probes." },
      { slug: "namespaces", title: "Namespaces", blurb: "Many isolated graphs in one Fallen-8, addressable under /ns/{name}/." },
    ],
  },
  samples: {
    heading: "How samples work",
    links: [
      { slug: "samples", title: "Sample gallery", blurb: "One-click curated demo graphs, each a guided tour of a feature." },
      { slug: "graph-model", title: "Graph model", blurb: "The directed property graph the samples populate." },
    ],
  },
  "/save-games": {
    heading: "How save games work",
    links: [
      { slug: "save-games", title: "Save games", blurb: "Checkpoints tracked by a registry, on top of a write-ahead log." },
    ],
  },
  browser: {
    heading: "How the browser works",
    links: [
      { slug: "graph-model", title: "Graph model", blurb: "Vertices, edges, and typed properties, with REST CRUD and scans." },
      { slug: "namespaces", title: "Namespaces", blurb: "The browser reads within one isolated namespace at a time." },
      { slug: "bulk-import-export", title: "Bulk import and export", blurb: "Stream whole graphs as newline-delimited JSON." },
    ],
  },
  query: {
    heading: "How queries work",
    links: [
      { slug: "delegates", title: "Delegates", blurb: "No query language: filters and cost functions are compiled C#." },
      { slug: "stored-queries", title: "Stored queries", blurb: "Register a vetted, compiled query once and invoke it by name." },
      { slug: "api-reference", title: "API Reference", blurb: "The full REST surface, rendered interactively with Scalar." },
    ],
  },
  indexes: {
    heading: "How indexes work",
    links: [
      { slug: "indexes", title: "Indexes", blurb: "Dictionary, range, fulltext, spatial R-Tree, and vector kNN indexes." },
      { slug: "vector-search", title: "Vector search", blurb: "Exact k-nearest-neighbour over float[] embeddings." },
    ],
  },
  path: {
    heading: "How path finding works",
    links: [
      { slug: "path-finding", title: "Path finding", blurb: "Shortest and weighted paths with the BLS and Dijkstra algorithms." },
      { slug: "delegates", title: "Delegates", blurb: "Filter and cost functions are runtime-compiled C# fragments." },
      { slug: "semantic-traversal", title: "Semantic traversal", blurb: "Steer paths by similarity with a code-free semantic block." },
    ],
  },
  subgraphs: {
    heading: "How subgraphs work",
    links: [
      { slug: "subgraphs", title: "Subgraphs", blurb: "Extract a pattern-matched subset as a standalone graph." },
      { slug: "delegates", title: "Delegates", blurb: "Subgraph pattern filters are compiled C# fragments." },
      { slug: "semantic-traversal", title: "Semantic traversal", blurb: "Grow subgraphs by vector similarity to a query." },
    ],
  },
  analytics: {
    heading: "How analytics work",
    links: [
      { slug: "graph-analytics", title: "Graph analytics", blurb: "PageRank, components, communities, degree, and triangle counting." },
    ],
  },
  plugins: {
    heading: "How plugins work",
    links: [
      { slug: "plugins", title: "Plugins", blurb: "The extension model behind indices, algorithms, and services." },
      { slug: "plugin-registration", title: "Plugin registration", blurb: "Add algorithm and graph-function plugins from C# source at runtime." },
    ],
  },
  canvas: {
    heading: "How the canvas works",
    links: [
      { slug: "studio", title: "F8 Studio", blurb: "The visual canvas is part of the Studio workbench." },
      { slug: "graph-model", title: "Graph model", blurb: "What the canvas renders: vertices, edges, and properties." },
    ],
  },
  benchmarks: {
    heading: "How the benchmark works",
    links: [
      { slug: "benchmark", title: "Benchmark", blurb: "Measure raw edge-traversal throughput over the loaded graph." },
      { slug: "namespaces", title: "Namespaces", blurb: "Generation and measurement both act on the ACTIVE namespace, never on \"default\"." },
      { slug: "running", title: "Running Fallen-8", blurb: "Launch options and configuration that affect performance." },
    ],
  },
  "/integrations": {
    heading: "How integrations work",
    links: [
      { slug: "integrations", title: "Integrations", blurb: "Read a system on your own network and write what it saw into a namespace." },
      { slug: "running", title: "Running Fallen-8", blurb: "The compose variables that bring the runtime up and bound where a credential may go." },
      { slug: "architecture", title: "Architecture", blurb: "Where the runtime sits: a separate deployable with no host port." },
    ],
  },
  knowledge: {
    heading: "How the knowledge layer works",
    links: [
      { slug: "unstructured-ingestion", title: "Semantic layer", blurb: "Documents in, graph out: retrievable, traversable Document, Chunk, and Entity vertices." },
      { slug: "vector-search", title: "Vector search", blurb: "Similarity search over embeddings backs retrieval." },
      { slug: "semantic-traversal", title: "Semantic traversal", blurb: "Traverse the knowledge graph by semantic similarity." },
    ],
  },
};

/** Accessor: the help entry for a section leaf, or undefined when the leaf has no mapping. */
export const sectionHelp = (leaf: string | null | undefined): SectionHelpEntry | undefined =>
  leaf == null ? undefined : SECTION_HELP[leaf];
