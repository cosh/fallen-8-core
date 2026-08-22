// MIT License
//
// SettingRow.tsx
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

import { memo } from "react";
import type { SettingREST } from "../api/types";
import { environmentSpelling, isEnvironmentLocked, settingTestId } from "../lib/configCatalog";
import { RESTART_PENDING_CHIP } from "../lib/restartCopy";

/**
 * One configuration setting, rendered from its descriptor's `kind` (feature
 * writable-instance-config 5.1). Uses only the existing primitives (.input, .label, the shared badge
 * shape), so the editor introduces no new visual language.
 *
 * The row explains itself rather than the setting: what the value is, where it came from, and why it
 * cannot be edited when it cannot. It deliberately carries NO description of what a key means, because
 * that lives on the server's own documentation for the key and a second copy here would drift.
 *
 * The rules it reads from a descriptor (the test handle, the environment spelling, whether a source
 * outranks a stored value) live in lib/configCatalog.ts, because the configuration surface selects and
 * groups by the same rules and a second copy of them here would be the thing that drifts.
 */

function SourceBadge({ setting }: { setting: SettingREST }) {
  const locked = isEnvironmentLocked(setting);
  const tone = locked
    ? "border-warn/50 text-warn"
    : setting.source === "override"
      ? "border-accent/40 text-accent"
      : "border-line text-fg-faint";
  const label = setting.source === "override" ? "set here" : setting.source;

  return (
    <span className={`rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase ${tone}`}>
      {label}
    </span>
  );
}

// Memoised, and the callbacks carry the key so the surface can pass ONE stable pair: without that,
// every keystroke into any field re-renders every row in the open section because each would get
// fresh closures. Anything rendering these must keep its callbacks stable across a keystroke.
export const SettingRow = memo(function SettingRow({
  setting,
  draft,
  onChange,
  onClear,
  disabled,
}: {
  setting: SettingREST;
  /** The unsaved value, or undefined when this row is untouched. */
  draft?: string | null;
  onChange: (key: string, value: string) => void;
  onClear: (key: string) => void;
  /** True when the whole editable region is gated off (an embed host locked it). */
  disabled?: boolean;
}) {
  const locked = isEnvironmentLocked(setting);
  const writable = setting.tier !== "notWritable" && !locked && !disabled;
  const current = draft ?? setting.value ?? "";
  const dirty = draft !== undefined && draft !== (setting.value ?? "");
  const testId = settingTestId(setting.key);

  return (
    <div className="border-line/60 grid grid-cols-[minmax(0,18rem)_1fr] items-start gap-3 border-t py-2 first:border-t-0">
      <div className="min-w-0">
        <label className="label wrap-break-word" htmlFor={testId}>
          <code className="text-[11px] normal-case">{setting.key}</code>
        </label>
        <div className="mt-1 flex flex-wrap items-center gap-1.5">
          <SourceBadge setting={setting} />
          {setting.restartPending && (
            <span className="border-warn/50 text-warn rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase">
              {RESTART_PENDING_CHIP}
            </span>
          )}
        </div>
      </div>

      <div className="min-w-0">
        {setting.tier === "notWritable" ? (
          <div className="text-fg-faint text-[11px]" data-testid={`${testId}-reason`}>
            <span className="text-fg-dim">not writable</span>
            {setting.rule ? ` (${setting.rule})` : ""}: {setting.reason}
          </div>
        ) : (
          <>
            {renderControl()}
            {locked && (
              <div className="text-fg-faint mt-1 text-[10px]" data-testid={`${testId}-env`}>
                set by <code className="text-fg-faint text-[10px]">{environmentSpelling(setting.key)}</code> in
                the environment, which outranks anything stored here
              </div>
            )}
            {!locked && setting.source === "override" && (
              <button
                type="button"
                className="btn mt-1 normal-case"
                data-testid={`${testId}-clear`}
                disabled={disabled}
                onClick={() => onClear(setting.key)}
                title="Remove the stored value and fall back to whatever this instance is configured with"
              >
                Clear
              </button>
            )}
          </>
        )}
      </div>
    </div>
  );

  function renderControl() {
    const common = {
      id: testId,
      "data-testid": testId,
      disabled: !writable,
      className: dirty ? "input w-auto border-accent/60" : "input w-auto",
    };

    if (setting.kind === "bool") {
      return (
        <label className="label-help flex items-center gap-2 normal-case">
          <input
            type="checkbox"
            id={testId}
            data-testid={testId}
            disabled={!writable}
            checked={current === "true"}
            onChange={(event) => onChange(setting.key, event.target.checked ? "true" : "false")}
          />
          <span className="text-fg-dim text-[12px]">{current === "true" ? "true" : "false"}</span>
        </label>
      );
    }

    if (setting.kind === "enum") {
      return (
        <select
          {...common}
          value={current}
          onChange={(event) => onChange(setting.key, event.target.value)}
        >
          {(setting.allowedValues ?? []).map((allowed) => (
            <option key={allowed} value={allowed}>
              {allowed}
            </option>
          ))}
        </select>
      );
    }

    if (setting.kind === "int" || setting.kind === "double") {
      return (
        <input
          {...common}
          type="number"
          inputMode="numeric"
          min={setting.minimum ?? undefined}
          max={setting.maximum ?? undefined}
          step={setting.kind === "double" ? "any" : 1}
          value={current}
          onChange={(event) => onChange(setting.key, event.target.value)}
        />
      );
    }

    // string, and array (which the server never marks writable, so it arrives disabled).
    return (
      <input {...common} type="text" value={current} onChange={(event) => onChange(setting.key, event.target.value)} />
    );
  }
});

