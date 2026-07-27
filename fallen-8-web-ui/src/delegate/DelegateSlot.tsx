// MIT License
//
// DelegateSlot.tsx
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

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
  disabled = false,
  disabledReason,
}: {
  instance: InstanceConfig;
  delegateKind: DelegateKind;
  label: string;
  contextLabel: string;
  value: string;
  onChange: (fragment: string) => void;
  /** When true (the semantic block owns this slot), the slot is inert with a reason. */
  disabled?: boolean;
  disabledReason?: string;
}) {
  const [open, setOpen] = useState(false);
  const testId = `slot-${label.replace(/[^a-z0-9]+/gi, "-").toLowerCase()}`;

  if (disabled) {
    return (
      <div className="flex items-center gap-2 opacity-60" data-testid={`${testId}-disabled`}>
        <span className="text-fg-dim w-44 shrink-0 text-[11px] tracking-wider uppercase">
          {label}
        </span>
        <span className="text-fg-faint min-w-0 flex-1 truncate text-[11px]" title={disabledReason}>
          {disabledReason ?? "disabled"}
        </span>
      </div>
    );
  }

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
        data-testid={testId}
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
