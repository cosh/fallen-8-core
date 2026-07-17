import type { ReactNode } from "react";
import { help, type FieldHelpKey } from "../lib/fieldHelp";

/**
 * Labeled field with hover help (feature studio-mutations-ux): the title sits on the
 * WRAPPER, so hovering the label or the input itself shows the explanation. Help copy
 * lives only in lib/fieldHelp.ts; call sites pass a key, never text.
 */
export function Field({
  helpKey,
  label,
  htmlFor,
  className,
  children,
}: {
  helpKey: FieldHelpKey;
  label: ReactNode;
  htmlFor?: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className={className} title={help(helpKey)}>
      <label className="label label-help" htmlFor={htmlFor}>
        {label}
      </label>
      {children}
    </div>
  );
}
