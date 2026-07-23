import type { ReactNode } from "react";

/** The one truncation indicator: an amber chip stating what was cut and where. */
export function TruncationBadge({ children, testId }: { children: ReactNode; testId?: string }) {
  return (
    <span
      data-testid={testId}
      className="border-warn/50 text-warn rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase"
    >
      {children}
    </span>
  );
}
