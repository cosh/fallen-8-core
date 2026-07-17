import type { PropertySpecification } from "../api/types";
import { toPropertySpec, validateTypedValue, type TypedValue } from "../lib/literals";
import { help } from "../lib/fieldHelp";
import { Field } from "./Field";
import { TypedLiteralEditor } from "./TypedLiteralEditor";

/**
 * 0..n initial properties for the create-vertex/edge forms (feature
 * studio-mutations-ux): rows of property id + typed value, validated as a set so the
 * submit button can gate on propertyRowsError().
 */

export interface PropertyRow {
  /** Stable per-row React key, minted by newPropertyRow(). */
  key: number;
  propertyId: string;
  value: TypedValue;
}

let nextRowKey = 0;

export const newPropertyRow = (): PropertyRow => ({
  key: nextRowKey++,
  propertyId: "",
  value: { type: "System.String", raw: "" },
});

/**
 * Why duplicates are rejected client-side: the wire format is a list, but the server
 * builds a one-value-per-key property dictionary from it and rejects a duplicate id
 * before the transaction is even enqueued — with a much murkier error than this one.
 */
export function propertyRowsError(rows: PropertyRow[]): string | null {
  const seen = new Set<string>();
  for (const row of rows) {
    const id = row.propertyId.trim();
    if (!id) return "Every property row needs a property id.";
    if (seen.has(id)) return `Duplicate property id '${id}'.`;
    seen.add(id);
    if (validateTypedValue(row.value)) return `Property '${id}': fix the value.`;
  }
  return null;
}

export const toPropertySpecs = (rows: PropertyRow[]): PropertySpecification[] | undefined =>
  rows.length === 0
    ? undefined
    : rows.map((row) => toPropertySpec(row.propertyId.trim(), row.value));

export function PropertyListEditor({
  rows,
  onChange,
  idPrefix,
}: {
  rows: PropertyRow[];
  onChange: (rows: PropertyRow[]) => void;
  idPrefix: string;
}) {
  const update = (key: number, patch: Partial<PropertyRow>) =>
    onChange(rows.map((row) => (row.key === key ? { ...row, ...patch } : row)));

  return (
    <div className="space-y-2" title={help("mutProperties")}>
      <div className="label label-help mb-0">properties (optional)</div>
      {rows.map((row) => (
        <div key={row.key} className="flex flex-wrap items-end gap-2">
          <Field
            helpKey="propertyId"
            label="property id"
            htmlFor={`${idPrefix}-prop-${row.key}`}
          >
            <input
              id={`${idPrefix}-prop-${row.key}`}
              data-testid={`${idPrefix}-prop-${row.key}-id`}
              className="input w-32"
              value={row.propertyId}
              onChange={(e) => update(row.key, { propertyId: e.target.value })}
              placeholder="age"
            />
          </Field>
          <TypedLiteralEditor
            label="value"
            idPrefix={`${idPrefix}-prop-${row.key}`}
            value={row.value}
            onChange={(value) => update(row.key, { value })}
          />
          <button
            type="button"
            className="btn"
            aria-label="remove property row"
            title="Remove this property row"
            onClick={() => onChange(rows.filter((r) => r.key !== row.key))}
          >
            ✕
          </button>
        </div>
      ))}
      <button
        type="button"
        className="btn"
        data-testid={`${idPrefix}-add-property`}
        onClick={() => onChange([...rows, newPropertyRow()])}
      >
        + Add property
      </button>
    </div>
  );
}
