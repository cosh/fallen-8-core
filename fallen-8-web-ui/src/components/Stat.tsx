/**
 * One labeled stat cell (Dashboard counts, benchmark results, Graph shape numbers, and
 * value tiles fed user/algorithm-controlled strings such as the embedding model id or an
 * analytics statistic key). Both lines truncate with a full-value title so an unbounded
 * label or value can never overflow the fixed-width tile.
 */
export function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="panel px-3 py-2">
      <div className="text-fg-faint truncate text-[10px] tracking-widest uppercase" title={label}>
        {label}
      </div>
      <div
        className="text-fg mt-1 truncate text-xl"
        data-testid={`stat-${label.replace(/\s/g, "-")}`}
        title={value}
      >
        {value}
      </div>
    </div>
  );
}
