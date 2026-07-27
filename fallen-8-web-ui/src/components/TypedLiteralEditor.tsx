// MIT License
//
// TypedLiteralEditor.tsx
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

import { LITERAL_TYPES, validateTypedValue, type TypedValue } from "../lib/literals";
import type { FieldHelpKey } from "../lib/fieldHelp";
import { Field } from "./Field";

/**
 * Typed-literal input (FR-9): a type selector + a validated value field, replacing
 * free-text JSON everywhere the API takes { value | propertyValue, fullQualifiedTypeName }.
 */
export function TypedLiteralEditor({
  label,
  value,
  onChange,
  idPrefix,
  helpKey = "typedValue",
}: {
  label: string;
  value: TypedValue;
  onChange: (value: TypedValue) => void;
  idPrefix: string;
  helpKey?: FieldHelpKey;
}) {
  const error = validateTypedValue(value);
  return (
    <Field helpKey={helpKey} label={label} htmlFor={`${idPrefix}-value`}>
      <div className="flex gap-1">
        <select
          aria-label={`${label} type`}
          className="input w-36"
          value={value.type}
          onChange={(e) => onChange({ ...value, type: e.target.value as TypedValue["type"] })}
        >
          {LITERAL_TYPES.map((t) => (
            <option key={t} value={t}>
              {t.replace("System.", "")}
            </option>
          ))}
        </select>
        <input
          id={`${idPrefix}-value`}
          data-testid={`${idPrefix}-value`}
          className={`input ${error ? "border-danger" : ""}`}
          value={value.raw}
          onChange={(e) => onChange({ ...value, raw: e.target.value })}
          placeholder={
            value.type === "System.Boolean"
              ? "true / false"
              : value.type === "System.DateTime"
                ? "2026-01-31T12:00:00"
                : "value"
          }
        />
      </div>
      {error && <div className="text-danger mt-1 text-[11px]">{error}</div>}
    </Field>
  );
}
