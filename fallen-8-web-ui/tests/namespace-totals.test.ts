// MIT License
//
// namespace-totals.test.ts
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

import { describe, expect, it } from "vitest";
import type { NamespaceEntry } from "../src/api/types";
import {
  describeDefaultOnly,
  describeTotals,
  describeWholeGraph,
  summarizeInventory,
} from "../src/lib/namespaceTotals";

/**
 * Feature instance-level-health: the aggregation behind the Instances row's health cell. The rules
 * under test are the honesty rules - a null count is UNKNOWN and never a zero, a partial sum says
 * so, and every reading names the scope it covers.
 */

const ns = (
  name: string,
  vertexCount: number | null,
  edgeCount: number | null,
): NamespaceEntry => ({
  name,
  state: vertexCount === null ? "notLoaded" : "ready",
  vertexCount,
  edgeCount,
  createdAt: "2026-08-23T10:00:00.000Z",
  loadOnStartupEnabled: null,
});

/** The reporter's own instance (measured 2026-08-23), the case the feature exists for. */
const REPORTED = [
  ns("Movie", 191, 1697),
  ns("default", 0, 0),
  ns("f8", 1013, 1774),
  ns("unify", 91, 115),
  ns("wind farm", 199, 344),
];

describe("summarizeInventory", () => {
  it("sums every namespace, so the reserved default's emptiness is not the instance's", () => {
    expect(summarizeInventory(REPORTED)).toEqual({
      namespaces: 5,
      vertices: 1494,
      edges: 3930,
      unreported: 0,
    });
  });

  it("skips a namespace that reports no counts and records how many it skipped", () => {
    const totals = summarizeInventory([...REPORTED, ns("archived", null, null)]);
    expect(totals).toEqual({ namespaces: 6, vertices: 1494, edges: 3930, unreported: 1 });
  });

  it("yields null counts, never zero, when nothing reported", () => {
    const totals = summarizeInventory([ns("a", null, null), ns("b", null, null)]);
    expect(totals).toEqual({ namespaces: 2, vertices: null, edges: null, unreported: 2 });
  });

  it("treats a half-reported entry as unknown rather than summing one dimension", () => {
    // The server moves the two counts together; a lopsided entry is unknown either way, and
    // adding its vertices while dropping its edges would produce a pair that describes no graph.
    const totals = summarizeInventory([ns("a", 5, 7), { ...ns("b", 3, 0), edgeCount: null }]);
    expect(totals).toEqual({ namespaces: 2, vertices: 5, edges: 7, unreported: 1 });
  });

  it("reports an empty instance as zeros, because a loaded empty graph really is zero", () => {
    expect(summarizeInventory([ns("default", 0, 0)])).toEqual({
      namespaces: 1,
      vertices: 0,
      edges: 0,
      unreported: 0,
    });
  });

  it("has no namespaces to sum on an empty inventory", () => {
    expect(summarizeInventory([])).toEqual({
      namespaces: 0,
      vertices: null,
      edges: null,
      unreported: 0,
    });
  });
});

describe("describeTotals", () => {
  it("groups the digits and names the scope", () => {
    const { label, title } = describeTotals(summarizeInventory(REPORTED));
    expect(label).toBe("5 ns · 1,494 v · 3,930 e");
    expect(title).toBe("Totals across all 5 namespaces on this instance.");
  });

  it("marks a partial sum as a lower bound and says how much is missing", () => {
    const { label, title } = describeTotals(
      summarizeInventory([...REPORTED, ns("archived", null, null)]),
    );
    expect(label).toBe("6 ns · >=1,494 v · >=3,930 e");
    expect(title).toContain("the 5 of 6 namespaces");
    expect(title).toContain("1 did not");
    expect(title).toContain("at least these");
  });

  it("renders the absent glyph, not a zero and not a bound, when nothing reported", () => {
    const { label, title } = describeTotals(
      summarizeInventory([ns("a", null, null), ns("b", null, null)]),
    );
    expect(label).toBe("2 ns · - v · - e");
    expect(label).not.toContain("0");
    expect(label).not.toContain(">=");
    expect(title).toContain("counts for none of its 2 namespaces");
  });

  it("keeps a genuine zero readable as zero, in the singular", () => {
    const { label, title } = describeTotals(summarizeInventory([ns("default", 0, 0)]));
    expect(label).toBe("1 ns · 0 v · 0 e");
    expect(title).toBe("Totals for the 1 namespace on this instance.");
  });

  it("says the inventory is empty rather than describing it as a size", () => {
    const { label, title } = describeTotals(summarizeInventory([]));
    expect(label).toBe("0 ns · - v · - e");
    expect(title).toBe("This instance lists no namespaces.");
  });
});

describe("the degraded readings", () => {
  it("states the whole graph for a server that predates namespaces", () => {
    const { label, title } = describeWholeGraph(7, 5);
    expect(label).toBe("7 v · 5 e");
    // No "ns" segment: there are no namespaces to count on such a server.
    expect(label).not.toContain("ns");
    expect(title).toContain("predates namespaces");
  });

  it("labels a probe-only reading as the default namespace, never as the instance", () => {
    const { label, title } = describeDefaultOnly(0, 0, "HTTP 500");
    expect(label).toBe("default: 0 v · 0 e");
    expect(title).toContain("HTTP 500");
    expect(title).toContain("not the instance total");
  });

  it("still refuses to invent zeros in a degraded reading of an unloaded default", () => {
    expect(describeDefaultOnly(null, null, "HTTP 500").label).toBe("default: - v · - e");
    expect(describeWholeGraph(null, null).label).toBe("- v · - e");
  });
});
