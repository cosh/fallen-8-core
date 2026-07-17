/** One labeled stat cell (Dashboard counts, benchmark results, Graph shape numbers). */
export function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="panel px-3 py-2">
      <div className="text-fg-faint text-[10px] tracking-widest uppercase">{label}</div>
      <div className="text-fg mt-1 text-xl" data-testid={`stat-${label.replace(/\s/g, "-")}`}>
        {value}
      </div>
    </div>
  );
}
