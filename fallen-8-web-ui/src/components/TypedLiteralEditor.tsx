import { LITERAL_TYPES, validateTypedValue, type TypedValue } from "../lib/literals";

/**
 * Typed-literal input (FR-9): a type selector + a validated value field, replacing
 * free-text JSON everywhere the API takes { value | propertyValue, fullQualifiedTypeName }.
 */
export function TypedLiteralEditor({
  label,
  value,
  onChange,
  idPrefix,
}: {
  label: string;
  value: TypedValue;
  onChange: (value: TypedValue) => void;
  idPrefix: string;
}) {
  const error = validateTypedValue(value);
  return (
    <div>
      <label className="label" htmlFor={`${idPrefix}-value`}>
        {label}
      </label>
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
    </div>
  );
}
