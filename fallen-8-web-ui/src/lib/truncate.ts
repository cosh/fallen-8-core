/**
 * Shortening arbitrary, user-controlled strings for display so they can never blow out a
 * view. A graph DB is full of unbounded text — namespace names (up to 63 chars), vertex/
 * edge labels, property keys AND values, index ids, stored-query names, file paths — and
 * any of them dumped raw can push a toolbar off-screen, widen a table past the viewport, or
 * wrap a flex row. The full value always belongs in a `title`/tooltip; see <Truncated>,
 * which is the component front-end for this and the one place display caps live.
 *
 * This is the char-count companion to the CSS `truncate` class: use it where CSS ellipsis is
 * awkward (table cells without a fixed layout, headings, chrome) or where a deterministic
 * cap is wanted. For long numeric vectors specifically, prefer `previewVector`.
 */

const ELLIPSIS = "…";

/**
 * The display caps, in characters, for each kind of user-controlled string — the one home
 * for "a number of chars that makes sense". Values are never lost: the full string always
 * rides in the title tooltip (see <Truncated>). Tune here, not per call site.
 */
export const DISPLAY_CAP = {
  /** Names/ids: namespace, instance, index, stored-query, subgraph, algorithm, plugin. */
  name: 40,
  /** Names inside fixed-width chrome (dropdown rows, badges, chips). */
  chipName: 28,
  /** Property keys / edge-property ids. */
  propertyKey: 40,
  /** Property values (a vector is pre-shrunk by previewVector first). */
  propertyValue: 80,
  /** CLR / fully-qualified type names. */
  typeName: 36,
  /** Vertex/edge labels. */
  label: 40,
  /** Path- / URL-shaped values (use middle mode so the tail stays readable). */
  path: 48,
  /** Free-form status/error lines interpolating user text. */
  message: 140,
} as const;

/**
 * Clips <paramref name="text"/> to at most <paramref name="max"/> characters (the ellipsis
 * included in that budget). End mode (default) keeps the head — `"fraud-quarterly" → "fraud-q…"`;
 * middle mode keeps both ends, best for paths/URLs whose tail carries meaning —
 * `"/ns/fraud-quarterly/status" → "/ns/fr…status"`.
 */
export function truncateChars(text: string, max: number, options: { middle?: boolean } = {}): string {
  if (max <= 1 || text.length <= max) {
    return text;
  }

  const budget = max - ELLIPSIS.length;
  if (budget <= 0) {
    return ELLIPSIS;
  }

  if (!options.middle) {
    return text.slice(0, budget).trimEnd() + ELLIPSIS;
  }

  const head = Math.ceil(budget / 2);
  const tail = budget - head;
  return text.slice(0, head) + ELLIPSIS + (tail > 0 ? text.slice(text.length - tail) : "");
}

/** Whether {@link truncateChars} would actually shorten this text (i.e. a title tooltip helps). */
export function isTruncated(text: string, max: number): boolean {
  return max > 1 && text.length > max;
}
