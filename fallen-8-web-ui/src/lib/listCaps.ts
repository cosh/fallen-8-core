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
 * Inline style that tells a `.scroll-list` wrapper how many rows to show before it caps its
 * height and scrolls. Custom CSS properties need the cast; this keeps it in one place.
 */
export function scrollRows(rows: number): CSSProperties {
  return { "--scroll-rows": rows } as unknown as CSSProperties;
}
