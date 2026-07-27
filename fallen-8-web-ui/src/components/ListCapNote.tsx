/**
 * Honest footer for the hard row ceiling: renders "showing N of M" ONLY when a list is actually
 * truncated at LIST_MAX_ROWS (see capList in lib/listCaps.ts) — a rare safety cap, never the
 * everyday scroll threshold — so a list that big never hides the overflow silently. Nothing
 * renders when everything fits (the normal case: the list just scrolls).
 */
export function ListCapNote({ shown, total }: { shown: number; total: number }) {
  if (total <= shown) return null;
  return (
    <div className="text-fg-faint px-3 py-1.5 text-[11px]" data-testid="list-cap-note">
      Showing the first {shown.toLocaleString()} of {total.toLocaleString()}.
    </div>
  );
}
