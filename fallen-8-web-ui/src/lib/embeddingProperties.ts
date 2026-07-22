import {
  EMBEDDING_PROPERTY_PREFIX as EMBEDDING_PREFIX,
  EMBEDDING_MODEL_PROPERTY_PREFIX as EMBEDDING_MODEL_PREFIX,
} from "../state/graphShape";
import { formatPropertyValue } from "./literals";
import { parseVector } from "./vector";

/** A property is reserved (embedding state) when it uses either embedding prefix. */
export function isReservedEmbeddingProperty(propertyId: string): boolean {
  return (
    propertyId.startsWith(EMBEDDING_PREFIX) || propertyId.startsWith(EMBEDDING_MODEL_PREFIX)
  );
}

/** A one-line preview of a stored vector value. The REST egress sends Single[] values as
 * the bracketed string form (see AGraphElement.FormatPropertyValue), so both shapes are
 * truncated — a 1024-dim embedding must never dump raw into the table. */
export function previewVector(value: unknown): string {
  let components: unknown[] | null = null;
  if (Array.isArray(value)) {
    components = value;
  } else if (typeof value === "string" && value.trim().startsWith("[")) {
    const parsed = parseVector(value);
    if (parsed.ok) components = parsed.vector;
  }
  if (components === null) return formatPropertyValue(value);
  const head = components
    .slice(0, 4)
    .map((n) => (typeof n === "number" ? Number(n.toFixed(4)) : n))
    .join(", ");
  return `[${head}${components.length > 4 ? ", …" : ""}] (d=${components.length})`;
}
