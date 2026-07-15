import { useState } from "react";
import type { DelegateKind } from "../api/types";
import type { InstanceConfig } from "../instances/types";
import { DelegateEditor } from "./DelegateEditor";

/**
 * A fragment slot (FR-13/16): shows the committed fragment (or "empty = match all") and
 * opens the shared editor modal. Slots only ever hold fragments that passed validation
 * (or empty) because the editor blocks commit otherwise (FR-25).
 */
export function DelegateSlot({
  instance,
  delegateKind,
  label,
  contextLabel,
  value,
  onChange,
}: {
  instance: InstanceConfig;
  delegateKind: DelegateKind;
  label: string;
  contextLabel: string;
  value: string;
  onChange: (fragment: string) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="flex items-center gap-2">
      <span className="text-fg-dim w-44 shrink-0 text-[11px] tracking-wider uppercase">
        {label}
      </span>
      <code
        className={`min-w-0 flex-1 truncate text-[11px] ${value ? "text-fg" : "text-fg-faint"}`}
        title={value || "empty = match everything"}
      >
        {value || "— empty (match everything)"}
      </code>
      {value && (
        <button type="button" className="btn" onClick={() => onChange("")}>
          Clear
        </button>
      )}
      <button
        type="button"
        className="btn"
        data-testid={`slot-${label.replace(/[^a-z0-9]+/gi, "-").toLowerCase()}`}
        onClick={() => setOpen(true)}
      >
        Edit
      </button>
      {open && (
        <DelegateEditor
          instance={instance}
          delegateKind={delegateKind}
          contextLabel={contextLabel}
          initialFragment={value}
          onCommit={(fragment) => {
            onChange(fragment);
            setOpen(false);
          }}
          onCancel={() => setOpen(false)}
        />
      )}
    </div>
  );
}
