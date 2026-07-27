/**
 * Studio-wide list caps — the one home for "how many rows a list may show".
 *
 * Policy: no in-app list grows the page without bound. Every collection is bounded on BOTH
 * axes — a row-count cap here (the rest reachable by narrowing/filtering, and surfaced by
 * <ListCapNote> so nothing is hidden silently) and a fixed scroll height via the `.scroll-list`
 * CSS wrapper (see index.css). Tune the numbers here, not per call site.
 */
export const LIST_CAP = {
  /** Any list without a reason to differ. */
  default: 100,
  /** Save-game registry (SaveGamesScreen). */
  saveGames: 50,
  /** Registered instances (ConnectScreen). */
  instances: 10,
  /** Namespaces (NamespacesPanel). */
  namespaces: 50,
} as const;

/**
 * Slice {@link items} to at most {@link cap} rows, reporting the original total so the call
 * site can render the honest "showing N of M" footer (see <ListCapNote>).
 */
export function capList<T>(
  items: readonly T[],
  cap: number,
): { shown: T[]; total: number } {
  return { shown: items.slice(0, cap), total: items.length };
}
