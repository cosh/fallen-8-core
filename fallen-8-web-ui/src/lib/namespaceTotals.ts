// MIT License
//
// namespaceTotals.ts
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

import type { NamespaceEntry } from "../api/types";
import { ABSENT, formatCountOrDash } from "./format";

/**
 * Instance-level size, summed over the namespace inventory (feature instance-level-health): an
 * INSTANCE is a collection of namespaces, so a number that describes one must cover all of them.
 * The rules that make this honest live here rather than in the component that renders them.
 */
export interface NamespaceTotals {
  /** Inventory size. Exact: `GET /ns` lists the catalog, not the residency filter. */
  namespaces: number;
  /**
   * Summed over the entries that report counts, or null when NONE does. Never a stand-in zero:
   * a namespace this server did not load reports null (feature namespace-startup-load), and
   * folding that into the sum as 0 would claim an empty graph over data still on disk.
   */
  vertices: number | null;
  /** Summed like {@link vertices}. */
  edges: number | null;
  /** How many entries reported no counts, i.e. how much the sums are missing. */
  unreported: number;
}

/** A rendered total: the cell's text, plus the tooltip that states which namespaces it covers. */
export interface TotalsDisplay {
  label: string;
  title: string;
}

/**
 * Sums an inventory. An entry counts as reporting only when BOTH counts are numbers - the server
 * moves them together (both null for a namespace it did not load), and a half-reported entry is
 * unknown either way, so it belongs in `unreported` rather than in a sum of one dimension.
 */
export function summarizeInventory(entries: NamespaceEntry[]): NamespaceTotals {
  let vertices: number | null = null;
  let edges: number | null = null;
  let unreported = 0;

  for (const entry of entries) {
    if (typeof entry.vertexCount === "number" && typeof entry.edgeCount === "number") {
      vertices = (vertices ?? 0) + entry.vertexCount;
      edges = (edges ?? 0) + entry.edgeCount;
    } else {
      unreported += 1;
    }
  }

  return { namespaces: entries.length, vertices, edges, unreported };
}

/** `V v · E e`, with the at-least marker when the sums are missing an entry. */
function counts(vertices: number | null, edges: number | null, partial: boolean): string {
  // The marker rides the number, not the absent glyph: ">=-" would be noise, and when nothing
  // reported there is no lower bound to state.
  const mark = (value: number | null) =>
    value === null ? ABSENT : `${partial ? ">=" : ""}${formatCountOrDash(value)}`;
  return `${mark(vertices)} v · ${mark(edges)} e`;
}

/** Why a namespace reports nothing, stated once. */
const UNREPORTED_REASON = "a namespace the server did not load reports none";

/**
 * The instance total: `N ns · V v · E e`. The tooltip always names the scope, because the bug this
 * replaced was a count whose scope was unstated (it silently meant the reserved `default`).
 */
export function describeTotals(totals: NamespaceTotals): TotalsDisplay {
  const { namespaces, vertices, edges, unreported } = totals;
  const label = `${namespaces} ns · ${counts(vertices, edges, unreported > 0)}`;

  // An inventory always contains the reserved default, so an empty one means the list itself is
  // the surprise - do not dress it up as a count.
  if (namespaces === 0) return { label, title: "This instance lists no namespaces." };

  if (vertices === null) {
    return {
      label,
      title:
        namespaces === 1
          ? `This instance reports no counts for its only namespace (${UNREPORTED_REASON}).`
          : `This instance reports counts for none of its ${namespaces} namespaces (${UNREPORTED_REASON}).`,
    };
  }
  // Reaching here with unreported > 0 means at least one namespace DID report, so this branch is
  // always plural.
  if (unreported > 0) {
    return {
      label,
      title: `Totals over the ${namespaces - unreported} of ${namespaces} namespaces this instance reports counts for; ${unreported} did not (${UNREPORTED_REASON}), so the real totals are at least these.`,
    };
  }
  return {
    label,
    title:
      namespaces === 1
        ? "Totals for the 1 namespace on this instance."
        : `Totals across all ${namespaces} namespaces on this instance.`,
  };
}

/**
 * A server that predates namespaces (`GET /ns` 404s): its bare routes ARE the whole graph, so the
 * probe's counts are already instance-level and carry no `ns` segment.
 */
export function describeWholeGraph(vertices: number | null, edges: number | null): TotalsDisplay {
  return {
    label: counts(vertices, edges, false),
    title: "This server predates namespaces, so these counts are its whole graph.",
  };
}

/**
 * The degraded reading: the inventory could not be read, so all we have is the probe, which is
 * namespace-scoped and aliases `default`. The label SAYS `default:` rather than passing one
 * namespace off as the instance - that substitution is the defect this feature exists to remove.
 */
export function describeDefaultOnly(
  vertices: number | null,
  edges: number | null,
  reason: string,
): TotalsDisplay {
  return {
    label: `default: ${counts(vertices, edges, false)}`,
    title: `The namespace inventory could not be read (${reason}), so this is the default namespace alone, not the instance total.`,
  };
}
