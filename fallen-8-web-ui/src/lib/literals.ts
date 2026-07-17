import type { LiteralSpecification, PropertySpecification } from "../api/types";

/**
 * Typed-literal editing (FR-9): everywhere the API takes
 * { value | propertyValue, fullQualifiedTypeName } the UI offers these types instead of
 * free-text JSON. Values always travel as strings; the server converts by type name.
 */

export const LITERAL_TYPES = [
  "System.String",
  "System.Int32",
  "System.Int64",
  "System.Double",
  "System.Boolean",
  "System.DateTime",
] as const;

export type LiteralTypeName = (typeof LITERAL_TYPES)[number];

export interface TypedValue {
  type: LiteralTypeName;
  raw: string;
}

/** Validates the raw text for its declared type; returns an error message or null. */
export function validateTypedValue(value: TypedValue): string | null {
  const raw = value.raw;
  switch (value.type) {
    case "System.String":
      return null;
    case "System.Int32": {
      if (!/^[+-]?\d+$/.test(raw.trim())) return "Expected a whole number.";
      const n = Number(raw);
      if (n < -2147483648 || n > 2147483647) return "Out of Int32 range.";
      return null;
    }
    case "System.Int64":
      return /^[+-]?\d+$/.test(raw.trim()) ? null : "Expected a whole number.";
    case "System.Double":
      return Number.isFinite(Number(raw.trim())) && raw.trim() !== ""
        ? null
        : "Expected a number.";
    case "System.Boolean":
      return /^(true|false)$/i.test(raw.trim()) ? null : "Expected true or false.";
    case "System.DateTime":
      return Number.isNaN(Date.parse(raw.trim())) ? "Expected a date/time." : null;
  }
}

/** Normalizes the raw text into the canonical wire string for its type. */
export function toWireValue(value: TypedValue): string {
  const raw = value.raw.trim();
  switch (value.type) {
    case "System.String":
      return value.raw;
    case "System.Boolean":
      return raw.toLowerCase() === "true" ? "true" : "false";
    case "System.Int32":
    case "System.Int64":
    case "System.Double":
      return raw;
    case "System.DateTime":
      return raw;
  }
}

export function toLiteral(value: TypedValue): LiteralSpecification {
  return { value: toWireValue(value), fullQualifiedTypeName: value.type };
}

export function toPropertySpec(propertyId: string, value: TypedValue): PropertySpecification {
  return {
    propertyId,
    propertyValue: toWireValue(value),
    fullQualifiedTypeName: value.type,
  };
}

/**
 * Parses the optional creation-date input on the mutation forms into the wire value:
 * a uint Unix timestamp in SECONDS. Empty keeps today's behaviour (0 = epoch); digits
 * are taken as seconds verbatim; anything else must parse as an ISO date/time.
 */
export function parseCreationDate(
  raw: string,
): { ok: true; seconds: number } | { ok: false; error: string } {
  const text = raw.trim();
  if (!text) return { ok: true, seconds: 0 };
  if (/^[+-]?\d+$/.test(text)) {
    const seconds = Number(text);
    if (seconds < 0 || seconds > 4294967295) {
      return { ok: false, error: "Out of range (uint seconds)." };
    }
    return { ok: true, seconds };
  }
  const ms = Date.parse(text);
  if (Number.isNaN(ms)) {
    return { ok: false, error: "Expected Unix seconds or an ISO date/time." };
  }
  const seconds = Math.floor(ms / 1000);
  if (seconds < 0 || seconds > 4294967295) {
    return { ok: false, error: "Out of range (uint seconds)." };
  }
  return { ok: true, seconds };
}

/** Renders a property value received from the API for table display. */
export function formatPropertyValue(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}
