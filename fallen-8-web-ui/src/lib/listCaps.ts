// MIT License
//
// listCaps.ts
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

import type { CSSProperties } from "react";

/**
 * Studio-wide list limits — two INDEPENDENT ideas, both living here:
 *
 *  1. LIST_MAX_ROWS — a hard safety ceiling. No list ever renders more than this many rows (a
 *     runaway-DOM guard). It is NOT a display cap you hit in normal use; on the rare occasion a
 *     list is bigger, <ListCapNote> says so. Everything below the ceiling is always rendered and
 *     reachable by scrolling.
 *
 *  2. SCROLL_ROWS — a per-list DISPLAY threshold, in ROWS. A list shorter than its threshold
 *     renders at its natural height with NO scrollbar; once it grows past the threshold its
 *     height is capped (to about that many rows) and a scrollbar appears, so you scroll through
 *     everything. Applied purely in CSS: a `.scroll-list` wrapper reads the count from the
 *     `--scroll-rows` custom property (see index.css and the `scrollRows` helper below).
 *
 * Tune the numbers here, not per call site.
 */
export const LIST_MAX_ROWS = 10_000;

/** Rows a list shows before it caps its height and scrolls. Add per-list entries as needed. */
export const SCROLL_ROWS = {
  /** Any list without a reason to differ. */
  default: 12,
  /** Save-game registry (SaveGamesScreen). */
  saveGames: 15,
  /** Registered instances (ConnectScreen). */
  instances: 8,
  /** Namespaces (NamespacesPanel). */
  namespaces: 12,
  /** Ingested documents (KnowledgeScreen). */
  documents: 12,
  /** Integrations this instance's runtime ships (IntegrationsScreen): a short, curated list. */
  integrations: 8,
  /** One run's diagnostics (IntegrationsScreen): a CSV with many bad rows can be long. */
  diagnostics: 10,
} as const;

/**
 * Slice {@link items} to the hard ceiling, reporting the true total so a call site can flag the
 * (rare) truncation via <ListCapNote>. This is the ONLY place a list's rows are ever dropped.
 */
export function capList<T>(
  items: readonly T[],
  max: number = LIST_MAX_ROWS,
): { shown: T[]; total: number } {
  return { shown: items.slice(0, max), total: items.length };
}

/**
 * How tall ONE row is, in rem, for a list whose rows are not a single line. The cap is a row
 * COUNT, so it needs a height per row to work with; the CSS default (2.5rem, see index.css) is a
 * one-line table row, and a list of wrapped prose hits that ceiling several rows early and scrolls
 * a list far shorter than its threshold. Add an entry only for a list that reads that way.
 */
export const SCROLL_ROW_REM = {
  /**
   * Available integrations (IntegrationsScreen): each row is a sentence describing what the
   * integration reads, and it wraps to three or four lines on a narrow window.
   */
  integrations: 5,
} as const;

/**
 * Inline style that tells a `.scroll-list` wrapper how many rows to show before it caps its
 * height and scrolls, and optionally how tall to assume one row is (see {@link SCROLL_ROW_REM};
 * omitted, the CSS default applies). Custom CSS properties need the cast; this keeps it in one place.
 */
export function scrollRows(rows: number, rowRem?: number): CSSProperties {
  const style: Record<string, string | number> = { "--scroll-rows": rows };
  if (rowRem !== undefined) {
    style["--scroll-row-h"] = `${rowRem}rem`;
  }

  return style as unknown as CSSProperties;
}
