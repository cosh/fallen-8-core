import { truncateChars } from "../lib/truncate";

/**
 * Renders a possibly-unbounded, user-controlled string clipped so it cannot blow out the
 * view, with the FULL value in the native title tooltip so nothing is lost. The one home for
 * "don't let user text destroy the layout" (feature graph-namespaces follow-up).
 *
 * Two modes:
 * - `max` given → a deterministic CHAR cap via {@link truncateChars} (chrome, headings,
 *   table cells without a fixed layout). Pass `middle` for path/URL-shaped values.
 * - `max` omitted → CSS ellipsis (`truncate`): the element must sit in a width-bounded flex
 *   parent (`min-w-0`, and usually `flex-1`), which adapts to the available space.
 */
export function Truncated({
  text,
  max,
  middle = false,
  className = "",
}: {
  text: string;
  max?: number;
  middle?: boolean;
  className?: string;
}) {
  const clipped = max === undefined ? text : truncateChars(text, max, { middle });
  // CSS mode can't know at render time whether the box will clip, so always offer the full
  // value; char mode only when it actually shortened.
  const title = max === undefined || clipped !== text ? text : undefined;
  const classes = max === undefined ? `truncate ${className}`.trim() : className;

  return (
    <span className={classes || undefined} title={title}>
      {clipped}
    </span>
  );
}
