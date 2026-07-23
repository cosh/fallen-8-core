import type { IndexDescription } from "../api/types";

/**
 * The client side of the index-capability contract (feature index-workspace): which
 * query forms the Query screen offers for an index. The server's /status inventory
 * reports capabilities derived from the index's interfaces; this module narrows them to
 * the families the UI knows and holds the fallback for servers predating the field.
 */

export type IndexCapability = "equality" | "range" | "fulltext" | "spatial" | "vector";

export const ALL_CAPABILITIES: readonly IndexCapability[] = [
  "equality",
  "range",
  "fulltext",
  "spatial",
  "vector",
];

/** Fallback map for the built-in plugin types when the server reports no capabilities. */
const BUILTIN_CAPABILITIES: Record<string, IndexCapability[]> = {
  DictionaryIndex: ["equality"],
  SingleValueIndex: ["equality"],
  RangeIndex: ["equality", "range"],
  RegExIndex: ["equality", "fulltext"],
  SpatialIndex: ["spatial"],
  VectorIndex: ["vector"],
};

/**
 * The query forms to offer. Unknown index (free-form id, no inventory entry) or unknown
 * third-party plugin on an old server: every form stays available — the server answers
 * 400 for a family the index does not serve, which the error box surfaces honestly.
 */
export function indexCapabilities(
  index: IndexDescription | undefined | null,
): readonly IndexCapability[] {
  if (!index) return ALL_CAPABILITIES;
  const reported = (index.capabilities ?? []).filter((c): c is IndexCapability =>
    (ALL_CAPABILITIES as readonly string[]).includes(c),
  );
  if (reported.length > 0) return reported;
  return BUILTIN_CAPABILITIES[index.pluginType ?? ""] ?? ALL_CAPABILITIES;
}
