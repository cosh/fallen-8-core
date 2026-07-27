/**
 * Honest footer for a capped list: renders "showing N of M" ONLY when the list was actually
 * truncated (see LIST_CAP / capList in lib/listCaps.ts), so a bounded list never hides rows
 * silently. Nothing renders when everything fits.
 */
export function ListCapNote({ shown, total }: { shown: number; total: number }) {
  if (total <= shown) return null;
  return (
    <div className="text-fg-faint px-3 py-1.5 text-[11px]" data-testid="list-cap-note">
      Showing the first {shown.toLocaleString()} of {total.toLocaleString()}.
    </div>
  );
}
